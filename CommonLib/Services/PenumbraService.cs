using System.Runtime.InteropServices;
using CommonLib.Interfaces;
using CommonLib.Models;
using Newtonsoft.Json;
using NLog;
using SevenZipExtractor;

namespace CommonLib.Services;

public class PenumbraService : IPenumbraService
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    private readonly IConfigurationService _configurationService;
    private readonly IFileStorage _fileStorage;

    private static readonly string[] PenumbraJsonLocations =
    {
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncher",
            "pluginConfigs",
            "Penumbra.json"
        ),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncherCN",
            "pluginConfigs",
            "Penumbra.json"
        ),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".xlcore",
            "pluginConfigs",
            "Penumbra.json"
        )
    };

    public PenumbraService(
        IConfigurationService configurationService,
        IFileStorage fileStorage)
    {
        _configurationService = configurationService;
        _fileStorage = fileStorage;
    }
    
    public void InitializePenumbraPath()
    {
        var existingPath = _configurationService.ReturnConfigValue(
            c => c.BackgroundWorker.PenumbraModFolderPath
        ) as string;

        if (!string.IsNullOrWhiteSpace(existingPath))
        {
            _logger.Info("Penumbra path is already set in the configuration.");
            return;
        }

        var foundPath = FindPenumbraPath();
        if (!string.IsNullOrWhiteSpace(foundPath))
        {
            UpdatePenumbraPathInConfiguration(foundPath);
        }
        else
        {
            _logger.Warn("Penumbra path could not be located with any known configuration.");
        }
    }
    
    public string InstallMod(string sourceFilePath)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath))
            throw new ArgumentNullException(nameof(sourceFilePath));

        var penumbraPath = _configurationService.ReturnConfigValue(
            c => c.BackgroundWorker.PenumbraModFolderPath
        ) as string;

        if (string.IsNullOrEmpty(penumbraPath))
            throw new InvalidOperationException("Penumbra path not configured. Make sure to set it first.");

        if (!_fileStorage.Exists(penumbraPath))
        {
            _fileStorage.CreateDirectory(penumbraPath);
        }

        var destinationFolderName = Path.GetFileNameWithoutExtension(sourceFilePath);
            using (var archiveForMeta = new ArchiveFile(sourceFilePath))
            {
                var metaEntry = archiveForMeta.Entries.FirstOrDefault(e =>
                    e?.FileName?.Equals("meta.json", StringComparison.OrdinalIgnoreCase) == true
                );

                if (metaEntry != null)
                {
                    var tempMetaFilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
                    archiveForMeta.Extract(entry =>
                    {
                        if (ReferenceEquals(entry, metaEntry))
                            return tempMetaFilePath;
                        return null;
                    });

                    try
                    {
                        var metaContent = _fileStorage.Read(tempMetaFilePath);
                        var meta = JsonConvert.DeserializeObject<PmpMeta>(metaContent);

                        if (!string.IsNullOrWhiteSpace(meta?.Name))
                        {
                            destinationFolderName = meta.Name;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "Failed to parse meta.json in {SourceFile}; using default folder name.",
                            sourceFilePath);
                    }
                    finally
                    {
                        if (_fileStorage.Exists(tempMetaFilePath))
                        {
                            _fileStorage.Delete(tempMetaFilePath);
                        }
                    }
                }
                else
                {
                    _logger.Warn("No meta.json found in {SourceFile}; using default folder name.", sourceFilePath);
                }
            }

        destinationFolderName = RemoveInvalidPathChars(destinationFolderName);

        var destinationFolderPath = Path.Combine(penumbraPath, destinationFolderName);
        if (!_fileStorage.Exists(destinationFolderPath))
        {
            _fileStorage.CreateDirectory(destinationFolderPath);
        }
        
        var extractionSuccessful = false;
        const int maxAttempts = 6;
        var attempt = 0;

        while (attempt < maxAttempts && !extractionSuccessful)
        {
            attempt++;
            if (attempt > 1)
            {
                _logger.Info("Retrying extraction attempt {Attempt} for {SourceFile}", attempt, sourceFilePath);
                ClearDirectory(destinationFolderPath);
            }
            
            using (var archive = new ArchiveFile(sourceFilePath))
            {
                archive.Extract(entry =>
                {
                    if (entry == null)
                        return null;
                    return Path.Combine(destinationFolderPath, entry.FileName ?? string.Empty);
                });
                
                ProcessBakFiles(destinationFolderPath);
                extractionSuccessful = true;
            }

            using (var archive = new ArchiveFile(sourceFilePath))
            {
                var expectedFiles = archive.Entries
                    .Where(e => e != null && !string.IsNullOrEmpty(e.FileName))
                    .Select(e => Path.Combine(destinationFolderPath, e.FileName))
                    .ToList();

                var allFilesExtracted = expectedFiles.All(file => _fileStorage.Exists(file));

                if (allFilesExtracted && expectedFiles.Count > 0)
                {
                    extractionSuccessful = true;
                    break;
                }
                else
                {
                    _logger.Warn("Extraction attempt {Attempt} did not extract all expected files for {SourceFile}", attempt, sourceFilePath);
                }
            }
        }

        if (!extractionSuccessful)
        {
            throw new InvalidOperationException($"Failed to fully extract mod files from archive {sourceFilePath} after {maxAttempts} attempts.");
        }

        _logger.Info("Installed archive from {Source} into {Destination}", sourceFilePath, destinationFolderPath);
        return destinationFolderPath;
    }

    public void ValidateAndCleanupBakFiles(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            _logger.Warn("Directory does not exist for .bak validation: {DirectoryPath}", directoryPath);
            return;
        }

        _logger.Info("Validating and cleaning up .bak files in {DirectoryPath}", directoryPath);
        ProcessBakFiles(directoryPath);
    }

    // This is a hack fix for a really eccentric bug where some mods have .json files turned into .json.bak
    private void ProcessBakFiles(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            _logger.Warn("Directory does not exist for .bak processing: {DirectoryPath}", directoryPath);
            return;
        }

        _logger.Info("Processing .bak files in {DirectoryPath}", directoryPath);
        
        var bakFiles = Directory.GetFiles(directoryPath, "*.bak", SearchOption.TopDirectoryOnly);
        
        _logger.Debug("Found {BakFileCount} .bak files in root directory: {DirectoryPath}", bakFiles.Length, directoryPath);
        
        if (bakFiles.Length == 0)
        {
            _logger.Debug("No .bak files found in root directory. Listing all files in root for debugging:");
            try
            {
                var allFiles = Directory.GetFiles(directoryPath, "*", SearchOption.TopDirectoryOnly);
                foreach (var file in allFiles)
                {
                    _logger.Debug("Found file: {FileName}", Path.GetFileName(file));
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to list files in root directory for debugging");
            }
        }
        
        var processedCount = 0;
        var renamedCount = 0;
        var deletedCount = 0;

        foreach (var bakFile in bakFiles)
        {
            _logger.Debug("Processing .bak file: {BakFile}", Path.GetFileName(bakFile));
            
            var originalFileName = bakFile.Substring(0, bakFile.Length - 4);
            
            try
            {
                if (!File.Exists(originalFileName))
                {
                    File.Move(bakFile, originalFileName);
                    renamedCount++;
                    _logger.Info("Renamed orphaned .bak file: {BakFile} -> {OriginalFile}", 
                        Path.GetFileName(bakFile), Path.GetFileName(originalFileName));
                }
                else
                {
                    File.Delete(bakFile);
                    deletedCount++;
                    _logger.Info("Deleted redundant .bak file: {BakFile} (original exists)", 
                        Path.GetFileName(bakFile));
                }
                processedCount++;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to process .bak file: {BakFile}", bakFile);
            }
        }

        _logger.Info("Completed .bak file processing: {ProcessedCount} processed, {RenamedCount} renamed, {DeletedCount} deleted", 
            processedCount, renamedCount, deletedCount);
    }

    private string FindPenumbraPath()
    {
        foreach (var location in PenumbraJsonLocations)
        {
            if (_fileStorage.Exists(location))
            {
                var path = ExtractPathFromJson(location);
                if (!string.IsNullOrWhiteSpace(path))
                    return path;
            }
        }

        return string.Empty;
    }
    
    private static string WineToRealPath(string winePath) =>
        winePath.StartsWith("Z:\\") ? winePath.Substring(2).Replace('\\', '/') : winePath;
    private string ExtractPathFromJson(string jsonFilePath)
    {
        var fileContent = _fileStorage.Read(jsonFilePath);
        var penumbraData = JsonConvert.DeserializeObject<PenumbraModPath>(fileContent);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return WineToRealPath(penumbraData?.ModDirectory ?? string.Empty);
        }
        {
            return penumbraData?.ModDirectory ?? string.Empty;
        }
    }

    private void UpdatePenumbraPathInConfiguration(string foundPath)
    {
        _logger.Info("Setting Penumbra path to {FoundPath}", foundPath);

        _configurationService.UpdateConfigValue(
            config => config.BackgroundWorker.PenumbraModFolderPath = foundPath,
            "BackgroundWorker.PenumbraModFolderPath",
            foundPath
        );
    }

    private static string RemoveInvalidPathChars(string text)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return new string(text.Where(ch => !invalidChars.Contains(ch)).ToArray());
    }
    
    private void ClearDirectory(string directoryPath)
    {
        try
        {
            var files = Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                if (_fileStorage.Exists(file))
                {
                    _fileStorage.Delete(file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "Failed to clear directory {DirectoryPath}", directoryPath);
        }
    }
}