using System.Collections.Concurrent;
using CommonLib.Enums;
using CommonLib.Interfaces;
using NLog;

namespace CommonLib.Services;

public class TexToolsHelper : ITexToolsHelper
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    private readonly IRegistryHelper _registryHelper;
    private readonly IConfigurationService _configurationService;
    private readonly IFileSystemHelper _fileSystemHelper;

    public TexToolsHelper(
        IRegistryHelper registryHelper,
        IConfigurationService configurationService,
        IFileSystemHelper fileSystemHelper)
    {
        _registryHelper = registryHelper;
        _configurationService = configurationService;
        _fileSystemHelper = fileSystemHelper;
    }

    /// <summary>
    /// Sets or retrieves the TexTools console path in the configuration
    /// </summary>
    public TexToolsStatus SetTexToolConsolePath()
    {
        var configuredPath = TryGetConfiguredPath();
        if (!string.IsNullOrEmpty(configuredPath))
        {
            _logger.Info("TexTools path already configured: {Path}", configuredPath);
            return TexToolsStatus.AlreadyConfigured;
        }

        var consoleToolPath = FindTexToolsConsolePath();
        if (string.IsNullOrEmpty(consoleToolPath))
        {
            _logger.Warn("TexTools installation not found");
            return TexToolsStatus.NotInstalled;
        }

        if (!_fileSystemHelper.FileExists(consoleToolPath))
        {
            _logger.Warn("ConsoleTools executable not found at: {Path}", consoleToolPath);
            return TexToolsStatus.NotFound;
        }

        _configurationService.UpdateConfigFromExternal(
            "BackgroundWorker.TexToolPath",
            consoleToolPath,
            "TexToolsHelper"
        );

        _logger.Info("Successfully configured TexTools path: {Path}", consoleToolPath);
        return TexToolsStatus.Found;
    }

    /// <summary>
    /// Returns path if already configured and valid, else null.
    /// </summary>
    private string TryGetConfiguredPath()
    {
        var configuredPath = (string)_configurationService.ReturnConfigValue(model => model.BackgroundWorker.TexToolPath);
        if (!string.IsNullOrEmpty(configuredPath) && _fileSystemHelper.FileExists(configuredPath))
        {
            return configuredPath;
        }
        return null;
    }

    /// <summary>
    /// Attempts registry, then standard paths, then heuristics, then full-drive search.
    /// </summary>
    private string FindTexToolsConsolePath()
    {
        var regPath = TryRegistryPath();
        if (!string.IsNullOrEmpty(regPath))
        {
            return regPath;
        }
        
        var standardPath = TryStandardPaths();
        if (!string.IsNullOrEmpty(standardPath))
        {
            return standardPath;
        }

        var likelyPath = TryLikelyFolders();
        if (!string.IsNullOrEmpty(likelyPath))
        {
            return likelyPath;
        }

        var scannedPath = TryFullDriveScan();
        if (!string.IsNullOrEmpty(scannedPath))
        {
            return scannedPath;
        }

        return null;
    }

    /// <summary>
    /// Checks for ConsoleTools.exe via registry (if supported).
    /// </summary>
    private string TryRegistryPath()
    {
        if (!_registryHelper.IsRegistrySupported)
            return null;

        _logger.Debug("Attempting to get TexTools path from registry...");
        var regPath = _registryHelper.GetTexToolRegistryValue();
        if (!string.IsNullOrWhiteSpace(regPath))
        {
            regPath = regPath.Trim('\"');
            var consoleToolRegPath = Path.Combine(regPath, "FFXIV_TexTools", "ConsoleTools.exe");
            if (_fileSystemHelper.FileExists(consoleToolRegPath))
            {
                _logger.Info("Found ConsoleTools.exe via registry: {Path}", consoleToolRegPath);
                return consoleToolRegPath;
            }
            _logger.Debug("Not found at registry path: {Path}", consoleToolRegPath);
        }
        return null;
    }

    /// <summary>
    /// Checks standard installation paths
    /// </summary>
    private string TryStandardPaths()
    {
        _logger.Debug("Checking standard TexTools installation paths...");
        var standardPaths = _fileSystemHelper.GetStandardTexToolsPaths();
        foreach (var standardPath in standardPaths)
        {
            if (_fileSystemHelper.FileExists(standardPath))
            {
                _logger.Info("Found ConsoleTools.exe at standard path: {Path}", standardPath);
                return standardPath;
            }
        }
        return null;
    }

    /// <summary>
    /// Searches likely user folders first.
    /// </summary>
    private string TryLikelyFolders()
    {
        _logger.Debug("Checking likely folders for TexTools...");
        var likelyFolders = new[]
        {
            @"C:\Program Files",
            @"C:\Program Files (x86)",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Documents"
        };

        foreach (var folder in likelyFolders)
        {
            try
            {
                _logger.Debug($"Searching in likely folder: {folder}");
                if (Directory.Exists(folder))
                {
                    var found = Directory.EnumerateFiles(folder, "ConsoleTools.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (!string.IsNullOrEmpty(found))
                    {
                        _logger.Info("Found ConsoleTools.exe in likely folder: {Path}", found);
                        return found;
                    }
                }
                else
                {
                    _logger.Debug($"Skipped nonexistent folder: {folder}");
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, $"Error occurred while searching in '{folder}'");
            }
        }
        return null;
    }

    /// <summary>
    /// Full-drive, parallelised, early stopping search with exclusions and extensive logging.
    /// </summary>
    private string TryFullDriveScan()
    {
        _logger.Debug("ConsoleTools.exe not found in likely folders, falling back to parallel drive search.");
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
            .ToArray();

        var cts = new CancellationTokenSource();
        var result = new ConcurrentBag<string>();
        var searchTasks = new List<Task>();

        foreach (var drive in drives)
        {
            var driveRoot = drive.RootDirectory.FullName;
            searchTasks.Add(Task.Run(() =>
            {
                try
                {
                    _logger.Debug($"Starting drive scan: {driveRoot}");
                    SearchRecursively(driveRoot, result, cts);
                }
                catch (OperationCanceledException)
                {
                    _logger.Debug($"Search canceled for {driveRoot}");
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, $"Exception encountered while searching drive {driveRoot}");
                }
            }, cts.Token));
        }

        try
        {
            Task.WaitAll(searchTasks.ToArray());
        }
        catch (AggregateException agEx)
        {
            foreach (var ex in agEx.InnerExceptions)
            {
                _logger.Warn(ex, "Aggregate exception during drive scan");
            }
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "Unexpected exception during drive search wait.");
        }

        var firstFound = result.FirstOrDefault();
        if (!string.IsNullOrEmpty(firstFound))
        {
            _logger.Info("ConsoleTools.exe found during drive scan: {Path}", firstFound);
            return firstFound;
        }
        _logger.Info("ConsoleTools.exe could not be found after full scan.");
        return null;
    }

    /// <summary>
    /// Recursively searches folders, skips protected, logs progress/errors, cancels on first find.
    /// </summary>
    private void SearchRecursively(string dir, ConcurrentBag<string> foundPath, CancellationTokenSource cts)
    {
        if (!foundPath.IsEmpty || cts.IsCancellationRequested)
            return;

        string[] protectedDirs = { "Windows", "ProgramData", "System Volume Information", "Recovery", "$Recycle.Bin", "PerfLogs" };

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "ConsoleTools.exe", SearchOption.TopDirectoryOnly))
            {
                _logger.Info($"Found ConsoleTools.exe at: {file}");
                foundPath.Add(file);
                cts.Cancel();
                return;
            }

            foreach (var subDir in Directory.EnumerateDirectories(dir))
            {
                if (foundPath.IsEmpty && !cts.IsCancellationRequested)
                {
                    string sub = Path.GetFileName(subDir);
                    if (protectedDirs.Any(p => string.Equals(p, sub, StringComparison.OrdinalIgnoreCase)))
                    {
                        _logger.Debug($"Skipping protected/system directory: {subDir}");
                        continue;
                    }
                    SearchRecursively(subDir, foundPath, cts);
                }
                else
                {
                    return;
                }
            }
        }
        catch (UnauthorizedAccessException uae)
        {
            _logger.Warn($"Access denied to directory {dir}: {uae.Message}");
        }
        catch (PathTooLongException ptle)
        {
            _logger.Warn($"Path too long {dir}: {ptle.Message}");
        }
        catch (IOException ioex)
        {
            _logger.Error($"I/O error in directory {dir}: {ioex.Message}");
        }
        catch (OperationCanceledException)
        {
            // Expected cancellation
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"Unexpected error while traversing {dir}");
        }
    }
}