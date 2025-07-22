using System.Runtime.InteropServices;
using CommonLib.Interfaces;
using CommonLib.Models;
using NLog;
using SevenZipExtractor;

namespace CommonLib.Services;

public class DownloadUpdater : IDownloadUpdater
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly IUpdateService _updateService;
    private readonly IAria2Service _aria2Service;

    public DownloadUpdater(IUpdateService updateService, IAria2Service aria2Service)
    {
        _updateService = updateService;
        _aria2Service = aria2Service;
    }

    public async Task<string?> DownloadAndExtractLatestUpdaterAsync(CancellationToken ct, IProgress<DownloadProgress>? progress = null)
    {
        try
        {
            _logger.Debug("=== UPDATER DOWNLOAD STARTED ===");
            _logger.Info("Progress reporter is {ProgressStatus}", progress != null ? "PROVIDED" : "NULL");
            
            progress?.Report(new DownloadProgress { Status = "Initializing updater download...", PercentComplete = 0 });
            
            await _aria2Service.EnsureAria2AvailableAsync(ct).ConfigureAwait(false);

            var downloadFolder = Path.Combine(Path.GetTempPath(), "Council Of Tsukuyomi");
            progress?.Report(new DownloadProgress { Status = "Cleaning up old updater files...", PercentComplete = 5 });
            CleanupOldUpdaterFiles(downloadFolder);

            progress?.Report(new DownloadProgress { Status = "Getting latest updater release...", PercentComplete = 10 });

            var latestRelease = await _updateService.GetLatestReleaseAsync(false, "CouncilOfTsukuyomi/Updater");
            if (latestRelease == null)
            {
                _logger.Warn("No releases returned. Aborting updater download.");
                progress?.Report(new DownloadProgress { Status = "No updater releases found", PercentComplete = 0 });
                return null;
            }

            _logger.Debug("Validating updater release author for security...");
            progress?.Report(new DownloadProgress { Status = "Validating updater security...", PercentComplete = 15 });
            
            var (isValid, errorMessage) = await _updateService.ValidateReleaseAuthorAsync(latestRelease, "CouncilOfTsukuyomi/Updater");
            if (!isValid)
            {
                _logger.Error("SECURITY ALERT: Updater download blocked - {SecurityError}", errorMessage);
                progress?.Report(new DownloadProgress { Status = "Security validation failed - updater blocked", PercentComplete = 0 });
                
                throw new UpdateService.SecurityException(errorMessage, 
                    latestRelease.Author?.Login ?? "unknown", 
                    latestRelease.TagName, 
                    "CouncilOfTsukuyomi/Updater");
            }

            _logger.Info("Updater release security validation passed for version {Version} by trusted author {Author}", 
                latestRelease.TagName, latestRelease.Author?.Login);

            if (latestRelease.Assets == null || latestRelease.Assets.Count == 0)
            {
                _logger.Warn("Release found, but no assets available for download. Aborting.");
                progress?.Report(new DownloadProgress { Status = "No updater assets found", PercentComplete = 0 });
                return null;
            }

            var platformSuffix = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux";
            var zipAsset = latestRelease.Assets
                .FirstOrDefault(a => a.Name.Contains($"Updater-{platformSuffix}", StringComparison.OrdinalIgnoreCase) && 
                                   a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

            if (zipAsset == null)
            {
                _logger.Warn("No {Platform} updater zip asset found in the release. Aborting updater download.", platformSuffix);
                progress?.Report(new DownloadProgress { Status = $"No {platformSuffix} updater zip found", PercentComplete = 0 });
                return null;
            }

            _logger.Info("Found TRUSTED updater asset: {Name}, Download URL: {Url}", zipAsset.Name, zipAsset.BrowserDownloadUrl);

            progress?.Report(new DownloadProgress { Status = "Preparing trusted updater download...", PercentComplete = 20 });

            if (!Directory.Exists(downloadFolder))
            {
                Directory.CreateDirectory(downloadFolder);
                _logger.Debug("Created temp folder at `{DownloadFolder}`.", downloadFolder);
            }
                
            _logger.Debug("Starting download of trusted updater asset...");
            
            IProgress<DownloadProgress>? downloadProgress = null;
            if (progress != null)
            {
                _logger.Info("Creating progress wrapper for updater download");
                downloadProgress = new Progress<DownloadProgress>(p => ReportDownloadProgress(p, progress));
            }
            else
            {
                _logger.Warn("No progress reporter available for updater download");
            }
            
            var downloadSucceeded = await _aria2Service.DownloadFileAsync(zipAsset.BrowserDownloadUrl, downloadFolder, ct, downloadProgress);
            if (!downloadSucceeded)
            {
                _logger.Error("Download failed for updater asset: {AssetName}", zipAsset.Name);
                progress?.Report(new DownloadProgress { Status = "Download failed", PercentComplete = 0 });
                return null;
            }
            _logger.Info("Trusted updater asset download complete.");

            var downloadedZipPath = Path.Combine(downloadFolder,
                Path.GetFileName(new Uri(zipAsset.BrowserDownloadUrl).AbsolutePath));
            _logger.Debug("Local path to downloaded zip: {DownloadedZipPath}", downloadedZipPath);

            progress?.Report(new DownloadProgress { Status = "Extracting trusted updater...", PercentComplete = 85 });

            try
            {
                _logger.Debug("Beginning extraction of the downloaded zip.");
                using (var archive = new ArchiveFile(downloadedZipPath))
                {
                    archive.Extract(downloadFolder, overwrite: true);
                }
                _logger.Info("Extraction completed successfully into `{DownloadFolder}`.", downloadFolder);
                
                progress?.Report(new DownloadProgress { Status = "Extraction complete", PercentComplete = 95 });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error encountered while extracting updater archive.");
                progress?.Report(new DownloadProgress { Status = "Extraction failed", PercentComplete = 0 });
                throw;
            }
            finally
            {
                if (File.Exists(downloadedZipPath))
                {
                    try
                    {
                        File.Delete(downloadedZipPath);
                        _logger.Debug("Removed temporary zip file `{DownloadedZipPath}`.", downloadedZipPath);
                    }
                    catch (Exception cleanupEx)
                    {
                        _logger.Warn(cleanupEx, "Failed to clean up the .zip file at `{DownloadedZipPath}`.", downloadedZipPath);
                    }
                }
            }

            var updaterExecutableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Updater.exe" : "Updater";
            var updaterPath = Path.Combine(downloadFolder, updaterExecutableName);
            
            if (File.Exists(updaterPath))
            {
                _logger.Debug("{UpdaterExecutable} found at `{UpdaterPath}`. Returning path.", updaterExecutableName, updaterPath);
                progress?.Report(new DownloadProgress { Status = "Trusted updater ready", PercentComplete = 100 });
                
                _logger.Info("=== TRUSTED UPDATER DOWNLOAD COMPLETED SUCCESSFULLY ===");
                return updaterPath;
            }

            _logger.Warn("{UpdaterExecutable} not found in `{DownloadFolder}` after extraction.", updaterExecutableName, downloadFolder);
            progress?.Report(new DownloadProgress { Status = "Updater executable not found", PercentComplete = 0 });
            return null;
        }
        catch (UpdateService.SecurityException ex)
        {
            _logger.Error(ex, "SECURITY VIOLATION: Updater download blocked - {SecurityError}", ex.Message);
            progress?.Report(new DownloadProgress { Status = $"Security Alert: {ex.Message}", PercentComplete = 0 });
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "An error occurred while downloading the updater");
            progress?.Report(new DownloadProgress { Status = "Error occurred", PercentComplete = 0 });
            return null;
        }
    }

    private void CleanupOldUpdaterFiles(string downloadFolder)
    {
        try
        {
            if (Directory.Exists(downloadFolder))
            {
                _logger.Info("Cleaning up old updater files from {DownloadFolder}", downloadFolder);
                
                var files = Directory.GetFiles(downloadFolder, "*", SearchOption.AllDirectories);
                var deletedCount = 0;
                
                foreach (var file in files)
                {
                    try
                    {
                        File.Delete(file);
                        deletedCount++;
                        _logger.Debug("Deleted old updater file: {File}", file);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "Failed to delete old updater file: {File}", file);
                    }
                }
                
                try
                {
                    var directories = Directory.GetDirectories(downloadFolder, "*", SearchOption.AllDirectories)
                        .OrderByDescending(d => d.Length);
                        
                    foreach (var directory in directories)
                    {
                        try
                        {
                            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                            {
                                Directory.Delete(directory);
                                _logger.Debug("Deleted empty updater directory: {Directory}", directory);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug(ex, "Could not delete directory: {Directory}", directory);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Error while cleaning up directories");
                }
                
                _logger.Info("Cleanup completed. Deleted {Count} old updater files", deletedCount);
            }
            else
            {
                _logger.Debug("Download folder does not exist, no cleanup needed: {DownloadFolder}", downloadFolder);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "Error occurred during updater cleanup, continuing with download...");
        }
    }
    
    private void ReportDownloadProgress(DownloadProgress individualProgress, IProgress<DownloadProgress> overallProgress)
    {
        _logger.Debug("=== UPDATER DOWNLOAD PROGRESS REPORT ===");
        _logger.Debug("Individual Progress: {Percent}%", individualProgress.PercentComplete);
        _logger.Debug("Downloaded: {Downloaded}/{Total} bytes", individualProgress.DownloadedBytes, individualProgress.TotalBytes);
        _logger.Debug("Speed: {Speed} bytes/sec", individualProgress.DownloadSpeedBytesPerSecond);
        _logger.Debug("Formatted Speed: {FormattedSpeed}", individualProgress.FormattedSpeed);
        _logger.Debug("Formatted Size: {FormattedSize}", individualProgress.FormattedSize);
        _logger.Debug("Status: {Status}", individualProgress.Status);

        var mappedProgress = 20 + (individualProgress.PercentComplete * 0.6);
        
        var status = $"Downloading trusted updater... {individualProgress.FormattedSize} at {individualProgress.FormattedSpeed}";

        _logger.Debug("Calculated mapped progress: {MappedProgress}%", mappedProgress);
        _logger.Debug("Status to report: {Status}", status);

        var progressToReport = new DownloadProgress
        {
            Status = status,
            PercentComplete = mappedProgress,
            DownloadSpeedBytesPerSecond = individualProgress.DownloadSpeedBytesPerSecond,
            ElapsedTime = individualProgress.ElapsedTime,
            TotalBytes = individualProgress.TotalBytes,
            DownloadedBytes = individualProgress.DownloadedBytes,
        };

        _logger.Debug("Reporting updater download progress to UI...");
        overallProgress.Report(progressToReport);
        _logger.Debug("=== END UPDATER DOWNLOAD PROGRESS REPORT ===");
    }
}