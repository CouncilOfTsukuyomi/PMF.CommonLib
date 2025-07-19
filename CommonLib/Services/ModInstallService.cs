using System.Diagnostics;
using System.Text;
using CommonLib.Exceptions;
using CommonLib.Extensions;
using CommonLib.Interfaces;
using CommonLib.Models;
using Newtonsoft.Json;
using NLog;

namespace CommonLib.Services;

public class ModInstallService : IModInstallService
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    private readonly HttpClient _httpClient;
    private readonly IStatisticService _statisticService;
    private readonly IPenumbraService _penumbraService;
    private readonly IConfigurationService _configurationService;
    private readonly IFileStorage _fileStorage;

    public ModInstallService(
        HttpClient httpClient,
        IStatisticService statisticService,
        IPenumbraService penumbraService,
        IConfigurationService configurationService,
        IFileStorage fileStorage)
    {
        _httpClient = httpClient;
        _statisticService = statisticService;
        _penumbraService = penumbraService;
        _configurationService = configurationService;
        _fileStorage = fileStorage;
    }

    public async Task<bool> InstallModAsync(string path)
    {
        var finalPath = ConvertIfNeeded(path);
        var extension = Path.GetExtension(finalPath);

        if (extension.Equals(".pmp", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _logger.Debug("Using PenumbraService for .pmp mod: {Path}", finalPath);
                _penumbraService.InitializePenumbraPath();
                var installedFolderPath = _penumbraService.InstallMod(finalPath);
                var modName = Path.GetFileName(installedFolderPath);
                try
                {
                    await ReloadModAsync(installedFolderPath, modName);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "ReloadModAsync failed for folder '{ModFolder}' with mod name '{ModName}'. This error is non-fatal.", installedFolderPath, modName);
                }

                var fileName = Path.GetFileName(finalPath);
                await _statisticService.RecordModInstallationAsync(fileName);

                if ((bool)_configurationService.ReturnConfigValue(config => config.BackgroundWorker.AutoDelete))
                {
                    _logger.Info("Deleting mod {Path}", finalPath);
                    _fileStorage.Delete(finalPath);
                }

                _logger.Info("Mod installed successfully from path '{Path}' via PenumbraService", finalPath);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error installing .pmp mod from path '{Path}'", finalPath);
                throw new ModInstallException($"Failed to install .pmp mod from path '{finalPath}'.", ex);
            }
        }

        // Fallback for other file types
        var modData = new ModInstallData(finalPath);
        var json = JsonConvert.SerializeObject(modData);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        _logger.Debug("Sending POST request to {Url}", new Uri(_httpClient.BaseAddress!, "installmod"));
        try
        {
            var response = await _httpClient.PostAsync("installmod", content);
            response.EnsureSuccessStatusCode();

            _logger.Info("Mod installed successfully from path '{Path}'", finalPath);

            var fileName = Path.GetFileName(finalPath);
            await _statisticService.RecordModInstallationAsync(fileName);

            return true;
        }
        catch (HttpRequestException ex)
        {
            _logger.Error(ex, "HTTP request exception while installing mod from path '{Path}'", finalPath);
            throw new ModInstallException($"Failed to install mod from path '{finalPath}'.", ex);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unexpected exception while installing mod from path '{Path}'", finalPath);
            throw new ModInstallException($"An unexpected error occurred while installing mod from path '{finalPath}'.", ex);
        }
    }

    private async Task ReloadModAsync(string modFolder, string modName)
    {
        var data = new ModReloadData(modFolder, modName);
        var body = JsonConvert.SerializeObject(data);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        _logger.Debug("Posting to /reloadmod for folder '{ModFolder}', name '{ModName}'", modFolder, modName);

        var response = await _httpClient.PostAsync("reloadmod", content);
        response.EnsureSuccessStatusCode();

        // Give Penumbra a little time to refresh
        await Task.Delay(200);
        _logger.Info("Successfully reloaded Penumbra mod at '{ModFolder}' with name '{ModName}'", modFolder, modName);
    }

    private string ConvertIfNeeded(string originalPath)
    {
        var converterPath = (string)_configurationService.ReturnConfigValue(config => config.BackgroundWorker.TexToolPath);
        var relocateFiles = (bool)_configurationService.ReturnConfigValue(c => c.BackgroundWorker.RelocateFiles);

        if (string.IsNullOrWhiteSpace(converterPath) || !File.Exists(converterPath))
        {
            _logger.Info("Conversion tool not found or not configured. Using original path.");
            return originalPath;
        }

        var originalDirectory = Path.GetDirectoryName(originalPath) ?? string.Empty;
        string convertedFilePath;

        if (relocateFiles)
        {
            // Create 'Converted' folder if relocate is enabled
            var convertedDirectory = Path.Combine(originalDirectory, "Converted");
            Directory.CreateDirectory(convertedDirectory);

            var newFileName = Path.GetFileNameWithoutExtension(originalPath) + "_converted" + Path.GetExtension(originalPath);
            convertedFilePath = Path.Combine(convertedDirectory, newFileName);

            // Run the conversion process
            RunConversion(converterPath, originalPath, convertedFilePath);

            // Check if the converted file exists
            if (File.Exists(convertedFilePath))
            {
                _logger.Info("Converted file successfully created at: {Path}", convertedFilePath);
                CleanupOriginalIfNeeded(originalPath);
                return convertedFilePath;
            }

            _logger.Warn("No converted file found at: {Path}; using original path.", convertedFilePath);

            // Remove the 'Converted' folder if it's empty
            if (Directory.Exists(convertedDirectory) && !Directory.EnumerateFileSystemEntries(convertedDirectory).Any())
            {
                _logger.Info("Removing empty 'Converted' directory: {Path}", convertedDirectory);
                Directory.Delete(convertedDirectory);
            }
            return originalPath;
        }
        else
        {
            // If relocate is disabled, place the file next to the original using "_dt"
            var newFileName = Path.GetFileNameWithoutExtension(originalPath) + "_dt" + Path.GetExtension(originalPath);
            convertedFilePath = Path.Combine(originalDirectory, newFileName);

            RunConversion(converterPath, originalPath, convertedFilePath);

            if (File.Exists(convertedFilePath))
            {
                _logger.Info("Converted file successfully created at: {Path}", convertedFilePath);
                if (!IgnoreList.IgnoreListStrings.Contains(convertedFilePath, StringComparer.InvariantCultureIgnoreCase))
                {
                    IgnoreList.IgnoreListStrings.Add(convertedFilePath);
                    _logger.Info("File: {Path} added to ignore list", convertedFilePath);
                }
                CleanupOriginalIfNeeded(originalPath);
                return convertedFilePath;
            }

            _logger.Warn("No converted file found at: {Path}; using original path.", convertedFilePath);
            return originalPath;
        }
    }

    private void RunConversion(string converterPath, string originalPath, string convertedFilePath)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = converterPath,
                    Arguments = $"/upgrade \"{originalPath}\" \"{convertedFilePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var standardOutput = process.StandardOutput.ReadToEnd();
            var errorOutput = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                _logger.Error("Conversion process for file '{Path}' exited with code {ExitCode}. Standard output: {Output}, Error output: {ErrorOutput}", originalPath, process.ExitCode, standardOutput, errorOutput);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "An error occurred while converting path '{Path}'. Using original file.", originalPath);
        }
    }

    private void CleanupOriginalIfNeeded(string originalPath)
    {
        if ((bool)_configurationService.ReturnConfigValue(config => config.BackgroundWorker.AutoDelete))
        {
            if (File.Exists(originalPath))
            {
                File.Delete(originalPath);
                _logger.Info("Deleted original mod file at: {Path}", originalPath);
            }
        }
    }
}