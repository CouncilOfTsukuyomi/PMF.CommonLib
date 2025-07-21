using System.Net.Http.Headers;
using CommonLib.Extensions;
using CommonLib.Interfaces;
using CommonLib.Models;
using Newtonsoft.Json;
using NLog;

namespace CommonLib.Services;

public class UpdateService : IUpdateService
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    private readonly IConfigurationService _configurationService;
    private readonly HttpClient _httpClient;

    private static readonly Dictionary<string, long> TrustedAuthors = new(StringComparer.OrdinalIgnoreCase)
    {
        { "github-actions[bot]", 41898282L },
        { "github-actions", 41898282L }
    };

    private static readonly HashSet<string> SuspiciousPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "administrator", "root", "system", "github-admin", "github-system",
        "official", "verified", "trusted", "secure", "authentic", "github-bot"
    };

    private readonly SemaphoreSlim _rateLimitSemaphore = new(5, 5);
    private DateTime _lastApiCall = DateTime.MinValue;
    private readonly TimeSpan _minApiCallInterval = TimeSpan.FromMilliseconds(100);

    private readonly Dictionary<long, (string login, DateTime cached)> _userValidationCache = new();
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromHours(1);

    public UpdateService(IConfigurationService configurationService)
    {
        _configurationService = configurationService;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("CouncilOfTsukuyomi", "1.0"));
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public class GitHubRelease
    {
        [JsonProperty("tag_name")]
        public string TagName { get; set; } = string.Empty;

        [JsonProperty("prerelease")]
        public bool Prerelease { get; set; }

        [JsonProperty("assets")]
        public List<GitHubAsset> Assets { get; set; } = new();

        [JsonProperty("body")]
        public string Body { get; set; } = string.Empty;

        [JsonProperty("published_at")]
        public DateTime PublishedAt { get; set; }

        [JsonProperty("author")]
        public GitHubAuthor? Author { get; set; }
    }

    public class GitHubAuthor
    {
        [JsonProperty("login")]
        public string Login { get; set; } = string.Empty;

        [JsonProperty("type")]
        public string Type { get; set; } = string.Empty;

        [JsonProperty("id")]
        public long Id { get; set; }
    }

    public class GitHubAsset
    {
        [JsonProperty("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("size")]
        public long Size { get; set; }
    }

    public class GitHubUserInfo
    {
        [JsonProperty("login")]
        public string Login { get; set; } = string.Empty;

        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; } = string.Empty;

        [JsonProperty("created_at")]
        public DateTime CreatedAt { get; set; }
    }

    public class SecurityException : Exception
    {
        public string AuthorLogin { get; }
        public string ReleaseTag { get; }
        public string Repository { get; }

        public SecurityException(string message, string authorLogin, string releaseTag, string repository) 
            : base(message)
        {
            AuthorLogin = authorLogin;
            ReleaseTag = releaseTag;
            Repository = repository;
        }
    }

    private async Task<(bool IsValid, string ErrorMessage)> ValidateReleaseAuthorAsync(GitHubRelease release, string repository)
    {
        if (release.Author == null)
        {
            var errorMessage = $"Security violation: Release '{release.TagName}' from repository '{repository}' has no author information.";
            _logger.Error("SECURITY ALERT: {ErrorMessage}", errorMessage);
            return (false, errorMessage);
        }

        var authorLogin = release.Author.Login?.Trim();
        var authorId = release.Author.Id;
        
        if (string.IsNullOrWhiteSpace(authorLogin))
        {
            var errorMessage = $"Security violation: Release '{release.TagName}' from repository '{repository}' has empty author login.";
            _logger.Error("SECURITY ALERT: {ErrorMessage}", errorMessage);
            return (false, errorMessage);
        }

        if (ContainsSuspiciousPattern(authorLogin) && !TrustedAuthors.ContainsKey(authorLogin))
        {
            var errorMessage = $"Security violation: Release '{release.TagName}' author '{authorLogin}' contains suspicious patterns.";
            _logger.Error("SECURITY ALERT: Suspicious author pattern - Author: {Author}, ID: {AuthorId}, Release: {Release}, Repository: {Repository}", 
                authorLogin, authorId, release.TagName, repository);
            return (false, errorMessage);
        }

        if (!TrustedAuthors.ContainsKey(authorLogin))
        {
            var errorMessage = $"Security violation: Release '{release.TagName}' from repository '{repository}' was created by untrusted author '{authorLogin}' (ID: {authorId}). Only releases from trusted GitHub Actions are allowed.";
            _logger.Error("SECURITY ALERT: Untrusted author - Author: {Author}, ID: {AuthorId}, Release: {Release}, Repository: {Repository}", 
                authorLogin, authorId, release.TagName, repository);
            return (false, errorMessage);
        }

        var expectedUserId = TrustedAuthors[authorLogin];
        if (authorId != expectedUserId)
        {
            var errorMessage = $"Security violation: SPOOFING DETECTED! Release '{release.TagName}' claims to be from '{authorLogin}' but has incorrect user ID {authorId}. Expected ID: {expectedUserId}.";
            _logger.Error("CRITICAL SECURITY ALERT: User ID mismatch - Claimed Author: {Author}, Provided ID: {ProvidedId}, Expected ID: {ExpectedId}, Release: {Release}, Repository: {Repository}", 
                authorLogin, authorId, expectedUserId, release.TagName, repository);
            return (false, errorMessage);
        }

        var (isValidUser, validationError) = await VerifyGitHubUserIdAsync(authorLogin, authorId);
        if (!isValidUser)
        {
            var errorMessage = $"Security violation: GitHub API validation failed for author '{authorLogin}' (ID: {authorId}): {validationError}";
            _logger.Error("SECURITY ALERT: GitHub API validation failed - {ErrorMessage}", errorMessage);
            return (false, errorMessage);
        }

        if (release.Author.Type != "Bot" && release.Author.Type != "User")
        {
            var errorMessage = $"Security warning: Release '{release.TagName}' author '{authorLogin}' has unusual type '{release.Author.Type}' (ID: {authorId})";
            _logger.Warn("SECURITY WARNING: {ErrorMessage}", errorMessage);
        }

        if (authorId <= 0)
        {
            var errorMessage = $"Security violation: Release '{release.TagName}' author '{authorLogin}' has invalid user ID {authorId}";
            _logger.Error("SECURITY ALERT: Invalid user ID - {ErrorMessage}", errorMessage);
            return (false, errorMessage);
        }

        _logger.Info("Enhanced security validation passed: Release '{Release}' from repository '{Repository}' is from verified GitHub Actions bot '{Author}' (ID: {AuthorId})", 
            release.TagName, repository, authorLogin, authorId);
        
        return (true, string.Empty);
    }

    private async Task<(bool IsValid, string ErrorMessage)> VerifyGitHubUserIdAsync(string username, long userId)
    {
        try
        {
            if (_userValidationCache.TryGetValue(userId, out var cached))
            {
                if (DateTime.UtcNow - cached.cached < _cacheExpiry)
                {
                    var isValid = string.Equals(cached.login, username, StringComparison.OrdinalIgnoreCase);
                    if (isValid)
                    {
                        _logger.Debug("User validation cache hit - User {Username} (ID: {UserId}) verified from cache", username, userId);
                        return (true, string.Empty);
                    }
                    else
                    {
                        var error = $"User ID {userId} is cached but belongs to '{cached.login}', not '{username}'";
                        _logger.Error("SECURITY: Cache validation failed - {Error}", error);
                        return (false, error);
                    }
                }
                else
                {
                    _userValidationCache.Remove(userId);
                    _logger.Debug("Removed expired cache entry for user ID {UserId}", userId);
                }
            }

            var userApiUrl = $"https://api.github.com/user/{userId}";
            _logger.Debug("Verifying user identity via GitHub API: {Url}", userApiUrl);

            using var response = await SecureHttpGetAsync(userApiUrl);
            
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    var error = $"User ID {userId} does not exist on GitHub";
                    _logger.Error("SECURITY: GitHub user verification failed - {Error}", error);
                    return (false, error);
                }
                
                var errorContent = await response.Content.ReadAsStringAsync();
                var error2 = $"GitHub API request failed with status {response.StatusCode}: {errorContent}";
                _logger.Error("SECURITY: GitHub API error during user verification - {Error}", error2);
                return (false, error2);
            }

            var userInfo = await response.Content.ReadAsJsonAsync<GitHubUserInfo>();
            if (userInfo == null)
            {
                var error = "Failed to deserialize GitHub user information";
                _logger.Error("SECURITY: {Error}", error);
                return (false, error);
            }

            _userValidationCache[userId] = (userInfo.Login, DateTime.UtcNow);
            _logger.Debug("Cached user validation result for {Username} (ID: {UserId})", userInfo.Login, userId);

            if (!string.Equals(userInfo.Login, username, StringComparison.OrdinalIgnoreCase))
            {
                var error = $"SPOOFING DETECTED: User ID {userId} belongs to '{userInfo.Login}', not '{username}'";
                _logger.Error("CRITICAL SECURITY ALERT: {Error}", error);
                return (false, error);
            }

            _logger.Info("GitHub user identity verified: {Username} (ID: {UserId})", username, userId);
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            var error = $"Exception during GitHub user verification: {ex.Message}";
            _logger.Error(ex, "SECURITY: Error verifying GitHub user identity - {Error}", error);
            return (false, error);
        }
    }

    private bool ContainsSuspiciousPattern(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return true;

        foreach (var pattern in SuspiciousPatterns)
        {
            if (username.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (username.Contains("..") || username.Contains("--") || username.Contains("__"))
        {
            return true;
        }

        if (ContainsMixedScripts(username))
        {
            return true;
        }

        return false;
    }

    private bool ContainsMixedScripts(string username)
    {
        var hasLatin = false;
        var hasCyrillic = false;
        var hasGreek = false;

        foreach (var c in username)
        {
            if (c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z')
                hasLatin = true;
            else if (c >= 'а' && c <= 'я' || c >= 'А' && c <= 'Я')
                hasCyrillic = true;
            else if (c >= 'α' && c <= 'ω' || c >= 'Α' && c <= 'Ω')
                hasGreek = true;
        }

        var scriptCount = (hasLatin ? 1 : 0) + (hasCyrillic ? 1 : 0) + (hasGreek ? 1 : 0);
        return scriptCount > 1;
    }

    private (bool IsValid, string ErrorMessage) ValidateReleaseAuthor(GitHubRelease release, string repository)
    {
        try
        {
            var result = ValidateReleaseAuthorAsync(release, repository).GetAwaiter().GetResult();
            return result;
        }
        catch (Exception ex)
        {
            var errorMessage = $"Security validation failed with exception: {ex.Message}";
            _logger.Error(ex, "SECURITY: Exception during release author validation - {ErrorMessage}", errorMessage);
            return (false, errorMessage);
        }
    }

    private async Task<HttpResponseMessage> SecureHttpGetAsync(string url)
    {
        await _rateLimitSemaphore.WaitAsync();
        try
        {
            var timeSinceLastCall = DateTime.UtcNow - _lastApiCall;
            if (timeSinceLastCall < _minApiCallInterval)
            {
                await Task.Delay(_minApiCallInterval - timeSinceLastCall);
            }

            _lastApiCall = DateTime.UtcNow;
            _logger.Debug("Making secure HTTP request to: {Url}", url);
            
            var response = await _httpClient.GetAsync(url);
            _logger.Debug("HTTP request completed with status: {StatusCode}", response.StatusCode);
            return response;
        }
        finally
        {
            _rateLimitSemaphore.Release();
        }
    }

    public async Task<List<string>> GetUpdateZipLinksAsync(string currentVersion, string repository)
    {
        _logger.Debug("Entered `GetUpdateZipLinksAsync`. CurrentVersion: {CurrentVersion}, Repository: {Repository}", currentVersion, repository);

        try
        {
            if (string.IsNullOrWhiteSpace(currentVersion) || string.IsNullOrWhiteSpace(repository))
            {
                _logger.Error("Security: Invalid input parameters detected");
                throw new ArgumentException("Current version and repository cannot be null or empty");
            }

            var includePrerelease = (bool)_configurationService.ReturnConfigValue(c => c.Common.IncludePrereleases);
            _logger.Debug("IncludePrerelease: {IncludePrerelease}", includePrerelease);

            var latestRelease = await GetLatestReleaseAsync(includePrerelease, repository);
            if (latestRelease == null)
            {
                _logger.Debug("No GitHub releases found. Returning an empty list.");
                return new List<string>();
            }

            var (isValid, errorMessage) = await ValidateReleaseAuthorAsync(latestRelease, repository);
            if (!isValid)
            {
                throw new SecurityException(errorMessage, latestRelease.Author?.Login ?? "unknown", latestRelease.TagName, repository);
            }

            _logger.Debug("Latest release found: {TagName}. Checking if it is newer than current version.", latestRelease.TagName);

            if (IsVersionGreater(latestRelease.TagName, currentVersion))
            {
                _logger.Debug("A newer version is available: {TagName}. Current: {CurrentVersion}", latestRelease.TagName, currentVersion);

                var zipLinks = latestRelease.Assets
                    .Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    .Where(a => a.Size > 0 && a.Size < 500_000_000)
                    .Select(a => a.BrowserDownloadUrl)
                    .Where(url => Uri.IsWellFormedUriString(url, UriKind.Absolute))
                    .ToList();

                _logger.Debug("Found {Count} valid .zip asset(s) in the latest release.", zipLinks.Count);
                return zipLinks;
            }

            _logger.Debug("Current version is up-to-date. Returning an empty list.");
            return new List<string>();
        }
        catch (SecurityException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in GetUpdateZipLinksAsync: {Message}", ex.Message);
            throw;
        }
    }

    public async Task<bool> NeedsUpdateAsync(string currentVersion, string repository)
    {
        _logger.Debug("Entered `NeedsUpdateAsync`. CurrentVersion: {CurrentVersion}, Repository: {Repository}", currentVersion, repository);

        try
        {
            if (string.IsNullOrWhiteSpace(currentVersion) || string.IsNullOrWhiteSpace(repository))
            {
                _logger.Error("Security: Invalid input parameters detected");
                return false;
            }

            var includePrerelease = (bool)_configurationService.ReturnConfigValue(c => c.Common.IncludePrereleases);
            _logger.Debug("IncludePrerelease: {IncludePrerelease}", includePrerelease);

            var latestRelease = await GetLatestReleaseAsync(includePrerelease, repository);
            if (latestRelease == null)
            {
                _logger.Debug("No releases returned. No update needed.");
                return false;
            }

            var (isValid, errorMessage) = await ValidateReleaseAuthorAsync(latestRelease, repository);
            if (!isValid)
            {
                _logger.Warn("Update check blocked due to security validation failure: {ErrorMessage}", errorMessage);
                return false;
            }

            var result = IsVersionGreater(latestRelease.TagName, currentVersion);
            _logger.Debug("`IsVersionGreater` returned {Result} for latest release {TagName}", result, latestRelease.TagName);

            return result;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in NeedsUpdateAsync: {Message}", ex.Message);
            return false;
        }
    }

    public async Task<VersionInfo?> GetMostRecentVersionInfoAsync(string repository)
    {
        _logger.Debug("Entered `GetMostRecentVersionInfoAsync`. Repository: {Repository}", repository);

        try
        {
            if (string.IsNullOrWhiteSpace(repository))
            {
                _logger.Error("Security: Invalid repository parameter");
                throw new ArgumentException("Repository cannot be null or empty");
            }

            var includePrerelease = (bool)_configurationService.ReturnConfigValue(c => c.Common.IncludePrereleases);
            _logger.Debug("IncludePrerelease: {IncludePrerelease}", includePrerelease);

            var latestRelease = await GetLatestReleaseAsync(includePrerelease, repository);
            if (latestRelease == null)
            {
                _logger.Debug("No releases found. Returning null.");
                return null;
            }

            var (isValid, errorMessage) = await ValidateReleaseAuthorAsync(latestRelease, repository);
            if (!isValid)
            {
                throw new SecurityException(errorMessage, latestRelease.Author?.Login ?? "unknown", latestRelease.TagName, repository);
            }

            _logger.Debug("Latest release version found: {TagName}", latestRelease.TagName);

            var version = latestRelease.TagName;
            if (latestRelease.Prerelease)
            {
                _logger.Debug("Release is a prerelease. Appending -b to version.");
                version = $"{latestRelease.TagName}-b";
            }

            var versionInfo = new VersionInfo
            {
                Version = version,
                Changelog = latestRelease.Body ?? string.Empty,
                IsPrerelease = latestRelease.Prerelease,
                PublishedAt = latestRelease.PublishedAt
            };

            versionInfo.ParseChangelog();

            _logger.Debug("Returning version info for {Version} with {ChangeCount} changes and {DownloadCount} downloads", 
                versionInfo.Version, versionInfo.Changes.Count, versionInfo.AvailableDownloads.Count);

            return versionInfo;
        }
        catch (SecurityException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in GetMostRecentVersionInfoAsync: {Message}", ex.Message);
            throw;
        }
    }

    public async Task<List<VersionInfo>> GetAllVersionInfoSinceCurrentAsync(string currentVersion, string repository)
    {
        _logger.Debug("Entered `GetAllVersionInfoSinceCurrentAsync`. CurrentVersion: {CurrentVersion}, Repository: {Repository}", currentVersion, repository);

        try
        {
            if (string.IsNullOrWhiteSpace(currentVersion) || string.IsNullOrWhiteSpace(repository))
            {
                _logger.Error("Security: Invalid input parameters detected");
                return new List<VersionInfo>();
            }

            var includePrerelease = (bool)_configurationService.ReturnConfigValue(c => c.Common.IncludePrereleases);
            _logger.Debug("IncludePrerelease: {IncludePrerelease}", includePrerelease);
            
            var allReleases = await GetAllReleasesAsync(includePrerelease, repository);
            if (allReleases == null || !allReleases.Any())
            {
                _logger.Debug("No releases found. Returning empty list.");
                return new List<VersionInfo>();
            }

            _logger.Debug("Found {Count} total releases. Filtering for versions newer than {CurrentVersion}", allReleases.Count, currentVersion);

            var newerReleases = new List<GitHubRelease>();
            foreach (var release in allReleases.Where(release => IsVersionGreater(release.TagName, currentVersion)))
            {
                var (isValid, _) = await ValidateReleaseAuthorAsync(release, repository);
                if (isValid)
                {
                    newerReleases.Add(release);
                }
            }

            newerReleases = newerReleases.OrderBy(release => GetVersionSortKey(release.TagName)).ToList();

            _logger.Debug("Found {Count} trusted releases newer than current version", newerReleases.Count);

            var versionInfoList = new List<VersionInfo>();
            foreach (var release in newerReleases)
            {
                var version = release.TagName;
                if (release.Prerelease)
                {
                    version = $"{release.TagName}-b";
                }

                var versionInfo = new VersionInfo
                {
                    Version = version,
                    Changelog = release.Body ?? string.Empty,
                    IsPrerelease = release.Prerelease,
                    PublishedAt = release.PublishedAt
                };

                versionInfo.ParseChangelog();
                versionInfoList.Add(versionInfo);

                _logger.Debug("Added version info for {Version}", versionInfo.Version);
            }

            _logger.Debug("Returning {Count} version info objects", versionInfoList.Count);
            return versionInfoList;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in GetAllVersionInfoSinceCurrentAsync: {Message}", ex.Message);
            return new List<VersionInfo>();
        }
    }

    public async Task<string> GetConsolidatedChangelogSinceCurrentAsync(string currentVersion, string repository)
    {
        _logger.Debug("Entered `GetConsolidatedChangelogSinceCurrentAsync`. CurrentVersion: {CurrentVersion}, Repository: {Repository}", currentVersion, repository);

        try
        {
            var allVersions = await GetAllVersionInfoSinceCurrentAsync(currentVersion, repository);
            if (!allVersions.Any())
            {
                _logger.Debug("No newer trusted versions found. Returning empty changelog.");
                return string.Empty;
            }

            var consolidatedChangelog = new System.Text.StringBuilder();
            foreach (var version in allVersions)
            {
                consolidatedChangelog.AppendLine($"## {version.Version}");
                consolidatedChangelog.AppendLine($"*Released: {version.PublishedAt:yyyy-MM-dd}*");
                consolidatedChangelog.AppendLine();
                consolidatedChangelog.AppendLine(version.Changelog);
                consolidatedChangelog.AppendLine();
            }

            var result = consolidatedChangelog.ToString().Trim();
            _logger.Debug("Generated consolidated changelog with {Length} characters covering {Count} versions", result.Length, allVersions.Count);
            
            return result;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in GetConsolidatedChangelogSinceCurrentAsync: {Message}", ex.Message);
            return string.Empty;
        }
    }

    private async Task<List<GitHubRelease>?> GetAllReleasesAsync(bool includePrerelease, string repository)
    {
        _logger.Debug("Entered `GetAllReleasesAsync`. IncludePrerelease: {IncludePrerelease}, Repository: {Repository}", includePrerelease, repository);

        var allReleases = new List<GitHubRelease>();
        var page = 1;
        const int perPage = 100;
        const int maxPages = 10;

        while (page <= maxPages)
        {
            var url = $"https://api.github.com/repos/{repository}/releases?page={page}&per_page={perPage}";
            _logger.Debug("Fetching releases page {Page}. URL: {Url}", page, url);

            using var response = await SecureHttpGetAsync(url);
            _logger.Debug("GitHub releases GET request for page {Page} completed with status code {StatusCode}", page, response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.Error("Request for releases page {Page} did not succeed with status {StatusCode}. Response: {ErrorContent}",
                    page, response.StatusCode, errorContent);
                break;
            }

            List<GitHubRelease>? pageReleases;
            try
            {
                pageReleases = await response.Content.ReadAsJsonAsync<List<GitHubRelease>>();
            }
            catch (JsonSerializationException ex)
            {
                var content = await response.Content.ReadAsStringAsync();
                _logger.Error(ex, "Error during JSON deserialization for page {Page}. Actual response: {Content}", page, content);
                break;
            }

            if (pageReleases == null || !pageReleases.Any())
            {
                _logger.Debug("No more releases found on page {Page}. Breaking pagination loop.", page);
                break;
            }

            allReleases.AddRange(pageReleases);
            _logger.Debug("Added {Count} releases from page {Page}. Total releases so far: {Total}", pageReleases.Count, page, allReleases.Count);

            if (pageReleases.Count < perPage)
            {
                _logger.Debug("Page {Page} returned fewer than {PerPage} releases. End of pagination reached.", page, perPage);
                break;
            }

            page++;
        }

        _logger.Debug("Fetched {Count} total releases across {Pages} pages", allReleases.Count, page - 1);

        if (!includePrerelease)
        {
            var beforeFilter = allReleases.Count;
            allReleases = allReleases.Where(r => !r.Prerelease).ToList();
            _logger.Debug("Filtered out prereleases. Before: {Before}, After: {After}", beforeFilter, allReleases.Count);
        }

        return allReleases;
    }

    private (int major, int minor, int patch) GetVersionSortKey(string version)
    {
        var cleanVersion = version.StartsWith("v", StringComparison.OrdinalIgnoreCase) 
            ? version.Substring(1) 
            : version;

        var parts = cleanVersion.Split('.');
        if (parts.Length != 3)
        {
            return (0, 0, 0);
        }

        var major = int.TryParse(parts[0], out var maj) ? maj : 0;
        var minor = int.TryParse(parts[1], out var min) ? min : 0;
        var patch = int.TryParse(parts[2], out var pat) ? pat : 0;

        return (major, minor, patch);
    }
    
    public async Task<string> GetMostRecentVersionAsync(string repository)
    {
        _logger.Debug("Entered `GetMostRecentVersionAsync`. Repository: {Repository}", repository);

        try
        {
            var versionInfo = await GetMostRecentVersionInfoAsync(repository);
            if (versionInfo == null)
            {
                _logger.Debug("No trusted version info found. Returning empty string.");
                return string.Empty;
            }

            _logger.Debug("Latest trusted release version found: {Version}", versionInfo.Version);
            return versionInfo.Version;
        }
        catch (SecurityException ex)
        {
            _logger.Error(ex, "Security validation failed for GetMostRecentVersionAsync");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error in GetMostRecentVersionAsync: {Message}", ex.Message);
            throw;
        }
    }

    public async Task<GitHubRelease?> GetLatestReleaseAsync(bool includePrerelease, string repository)
    {
        _logger.Debug("Entered `GetLatestReleaseAsync`. IncludePrerelease: {IncludePrerelease}, Repository: {Repository}", includePrerelease, repository);

        var url = $"https://api.github.com/repos/{repository}/releases";
        _logger.Debug("Full releases URL: {Url}", url);

        using var response = await SecureHttpGetAsync(url);
        _logger.Debug("GitHub releases GET request completed with status code {StatusCode}", response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            _logger.Error("Request for releases did not succeed with status {StatusCode}. Response: {ErrorContent}",
                response.StatusCode, errorContent);
            return null;
        }

        List<GitHubRelease>? releases;
        try
        {
            releases = await response.Content.ReadAsJsonAsync<List<GitHubRelease>>();
        }
        catch (JsonSerializationException ex)
        {
            var content = await response.Content.ReadAsStringAsync();
            _logger.Error(ex, "Error during JSON deserialization. Actual response: {Content}", content);
            return null;
        }

        if (releases == null || releases.Count == 0)
        {
            _logger.Debug("No releases were deserialized or the list is empty.");
            return null;
        }

        _logger.Debug("Found {Count} releases. Filter prerelease: {FilterPrerelease}", releases.Count, !includePrerelease);

        var filtered = includePrerelease
            ? releases
            : releases.Where(r => !r.Prerelease).ToList();

        var latestRelease = filtered.FirstOrDefault();
        if (latestRelease == null)
        {
            _logger.Debug("No suitable release found after filtering prereleases.");
        }
        else
        {
            _logger.Debug("Using release with `tag_name` {TagName}", latestRelease.TagName);
        }

        return latestRelease;
    }

    private bool IsVersionGreater(string newVersion, string oldVersion)
    {
        _logger.Debug("`IsVersionGreater` check. New: {NewVersion}, Old: {OldVersion}", newVersion, oldVersion);

        if (string.IsNullOrWhiteSpace(newVersion) || string.IsNullOrWhiteSpace(oldVersion))
        {
            _logger.Debug("One or both version strings were null/empty. Returning false.");
            return false;
        }

        var cleanNewVersion = newVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase) 
            ? newVersion.Substring(1) 
            : newVersion;
        var cleanOldVersion = oldVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase) 
            ? oldVersion.Substring(1) 
            : oldVersion;

        _logger.Debug("Cleaned versions. New: {CleanNewVersion}, Old: {CleanOldVersion}", cleanNewVersion, cleanOldVersion);

        var splittedNew = cleanNewVersion.Split('.');
        var splittedOld = cleanOldVersion.Split('.');
        
        if (splittedNew.Length != 3 || splittedOld.Length != 3)
        {
            _logger.Debug("Version not in x.x.x format. Using ordinal compare.");
            return string.CompareOrdinal(cleanNewVersion, cleanOldVersion) > 0;
        }

        if (!int.TryParse(splittedNew[0], out var majorNew) ||
            !int.TryParse(splittedNew[1], out var minorNew) ||
            !int.TryParse(splittedNew[2], out var patchNew))
        {
            _logger.Debug("Error parsing newVersion to integers. Using ordinal compare.");
            return string.CompareOrdinal(cleanNewVersion, cleanOldVersion) > 0;
        }

        if (!int.TryParse(splittedOld[0], out var majorOld) ||
            !int.TryParse(splittedOld[1], out var minorOld) ||
            !int.TryParse(splittedOld[2], out var patchOld))
        {
            _logger.Debug("Error parsing oldVersion to integers. Using ordinal compare.");
            return string.CompareOrdinal(cleanNewVersion, cleanOldVersion) > 0;
        }

        if (majorNew > majorOld) return true;
        if (majorNew < majorOld) return false;

        if (minorNew > minorOld) return true;
        if (minorNew < minorOld) return false;

        return patchNew > patchOld;
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
        _rateLimitSemaphore?.Dispose();
    }
}