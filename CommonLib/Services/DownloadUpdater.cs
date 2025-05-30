﻿using CommonLib.Interfaces;
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

    public async Task<string?> DownloadAndExtractLatestUpdaterAsync(CancellationToken ct)
    {
        _logger.Debug("Entered `DownloadAndExtractLatestUpdaterAsync`...");
        await _aria2Service.EnsureAria2AvailableAsync(ct).ConfigureAwait(false);

        var latestRelease = await _updateService.GetLatestReleaseAsync(false, "CouncilOfTsukuyomi/Updater");
        if (latestRelease == null)
        {
            _logger.Warn("No releases returned. Aborting updater download.");
            return null;
        }

        if (latestRelease.Assets == null || latestRelease.Assets.Count == 0)
        {
            _logger.Warn("Release found, but no assets available for download. Aborting.");
            return null;
        }
            
        var zipAsset = latestRelease.Assets
            .FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (zipAsset == null)
        {
            _logger.Warn("No .zip asset found in the release. Aborting updater download.");
            return null;
        }

        _logger.Info("Found updater asset: {Name}, Download URL: {Url}", zipAsset.Name, zipAsset.BrowserDownloadUrl);

        // Create or obtain a download directory (could be a temp folder)
        var downloadFolder = Path.Combine(Path.GetTempPath(), "Council Of Tsukuyomi");
        if (!Directory.Exists(downloadFolder))
        {
            Directory.CreateDirectory(downloadFolder);
            _logger.Debug("Created temp folder at `{DownloadFolder}`.", downloadFolder);
        }
            
        _logger.Debug("Starting download of updater asset...");
        var downloadSucceeded = await _aria2Service.DownloadFileAsync(zipAsset.BrowserDownloadUrl, downloadFolder, ct);
        if (!downloadSucceeded)
        {
            _logger.Error("Download failed for updater asset: {AssetName}", zipAsset.Name);
            return null;
        }
        _logger.Info("Updater asset download complete.");

        // Build path to downloaded zip
        var downloadedZipPath = Path.Combine(downloadFolder,
            Path.GetFileName(new Uri(zipAsset.BrowserDownloadUrl).AbsolutePath));
        _logger.Debug("Local path to downloaded zip: {DownloadedZipPath}", downloadedZipPath);

        // Extract the zip in the same folder it was downloaded to
        try
        {
            _logger.Debug("Beginning extraction of the downloaded zip.");
            using (var archive = new ArchiveFile(downloadedZipPath))
            {
                archive.Extract(downloadFolder, overwrite: true);
            }
            _logger.Info("Extraction completed successfully into `{DownloadFolder}`.", downloadFolder);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error encountered while extracting updater archive.");
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

        // Return the path to the expected updater executable
        var updaterPath = Path.Combine(downloadFolder, "Updater.exe");
        if (File.Exists(updaterPath))
        {
            _logger.Debug("Updater.exe found at `{UpdaterPath}`. Returning path.", updaterPath);
            return updaterPath;
        }

        _logger.Warn("Updater.exe not found in `{DownloadFolder}` after extraction.", downloadFolder);
        return null;
    }
}