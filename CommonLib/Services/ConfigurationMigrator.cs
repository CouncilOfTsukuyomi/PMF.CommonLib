using AutoMapper;
using CommonLib.Consts;
using CommonLib.Enums;
using CommonLib.Interfaces;
using CommonLib.Models;
using LiteDB;
using Newtonsoft.Json;
using NLog;
using System.Reflection;
using CommonLib.Helper;
using System.Security.Cryptography;
using System.Text;

namespace CommonLib.Services;

internal class ConfigurationMigrator : IConfigurationMigrator
{
    private readonly IFileStorage _fileStorage;
    private readonly IMapper _mapper;
    private readonly Logger _logger;

    public ConfigurationMigrator(IFileStorage fileStorage, IMapper mapper, Logger logger)
    {
        _fileStorage = fileStorage;
        _mapper = mapper;
        _logger = logger;
    }

    public async Task MigrateAsync(string databasePath, Func<ConfigurationOperation, Task> queueOperation)
    {
        // Helper to read current DB config and compute hash
        string GetCurrentConfigHash()
        {
            try
            {
                var connectionString = $"Filename={databasePath};Connection=Shared;Timeout=00:00:05";
                using var database = new LiteDatabase(connectionString);
                var configurations = database.GetCollection<ConfigurationRecord>("configurations");
                var all = configurations.FindAll().ToList();
                if (!all.Any()) return string.Empty;

                var flat = all.ToDictionary(r => r.Key, r => (r.Value, r.ValueType));
                var model = ConfigurationFlattener.UnflattenConfiguration(flat);
                return ConfigurationHashUtil.ComputeHash(model);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to compute current configuration hash; proceeding without hash comparison");
                return string.Empty;
            }
        }

        void UpdateMetadata(string newHash)
        {
            try
            {
                var connectionString = $"Filename={databasePath};Connection=Shared;Timeout=00:00:05";
                using var database = new LiteDatabase(connectionString);
                var metaCol = database.GetCollection<ConfigurationMetadataRecord>("configuration_metadata");
                var meta = metaCol.FindById("meta") ?? new ConfigurationMetadataRecord { Id = "meta" };
                meta.LastMigrationUtc = DateTime.UtcNow;
                meta.LastMigrationVersion = (Assembly.GetEntryAssembly()?.GetName().Version?.ToString()) ?? "unknown";
                meta.LastConfigHash = newHash ?? string.Empty;
                metaCol.Upsert(meta);
                _logger.Info("Updated configuration metadata: LastMigrationUtc={Time}, Version={Version}", meta.LastMigrationUtc, meta.LastMigrationVersion);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to update configuration metadata");
            }
        }

        var legacyFilePath = $"{ConfigurationConsts.OldConfigPath}\\config.json";
        var newFilePath = ConfigurationConsts.ConfigurationFilePath;
        var currentHash = GetCurrentConfigHash();

        async Task HandleMigrationSourceAsync(string filePath, string changeDescription)
        {
            if (!_fileStorage.Exists(filePath)) return;

            try
            {
                _logger.Info("Migrating configuration from {FilePath}...", filePath);
                using var stream = _fileStorage.OpenRead(filePath);
                using var reader = new StreamReader(stream);
                var content = reader.ReadToEnd();
                var incoming = JsonConvert.DeserializeObject<ConfigurationModel>(content);

                if (incoming == null)
                {
                    _logger.Warn("Parsed configuration from {FilePath} was null; skipping", filePath);
                    return;
                }

                var incomingHash = ConfigurationHashUtil.ComputeHash(incoming);
                if (!string.IsNullOrEmpty(currentHash) && string.Equals(incomingHash, currentHash, StringComparison.Ordinal))
                {
                    _logger.Info("Migration skipped for {FilePath}: incoming configuration is identical to current DB config.");
                    UpdateMetadata(incomingHash);
                }
                else
                {
                    await queueOperation(new ConfigurationOperation
                    {
                        Type = OperationType.SaveConfiguration,
                        Configuration = incoming,
                        DetectChanges = true,
                        ChangeDescription = changeDescription,
                        Timestamp = DateTime.UtcNow,
                        SourceId = "migration",
                        SuppressEvents = false
                    });
                    UpdateMetadata(incomingHash);
                    _logger.Info("Queued migration write for {FilePath}", filePath);
                }

                // Backup and delete original file after processing
                var backupPath = filePath + ".backup";
                if (_fileStorage.Exists(backupPath))
                    _fileStorage.Delete(backupPath);
                using (var sourceStream = _fileStorage.OpenRead(filePath))
                using (var destStream = _fileStorage.OpenWrite(backupPath))
                {
                    sourceStream.CopyTo(destStream);
                }
                _fileStorage.Delete(filePath);
                _logger.Info("Migration completed. Original file backed up to {BackupPath}", backupPath);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to migrate configuration from {FilePath}", filePath);
            }
        }

        // New standard path
        await HandleMigrationSourceAsync(newFilePath, "Migrated from config file");

        // Fallback: migrate config from the old executable directory (pre-change location)
        var exeDirFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config-v3.json");
        await HandleMigrationSourceAsync(exeDirFilePath, "Migrated from exe-directory config file");

        // Legacy path
        if (_fileStorage.Exists(legacyFilePath))
        {
            try
            {
                _logger.Info("Migrating legacy configuration from {LegacyFilePath}", legacyFilePath);
                using var stream = _fileStorage.OpenRead(legacyFilePath);
                using var reader = new StreamReader(stream);
                var oldConfigJson = reader.ReadToEnd();
                var oldConfig = JsonConvert.DeserializeObject<OldConfigModel.OldConfigurationModel>(oldConfigJson);
                if (oldConfig != null)
                {
                    var mapped = _mapper.Map<ConfigurationModel>(oldConfig);
                    var incomingHash = ConfigurationHashUtil.ComputeHash(mapped);
                    if (!string.IsNullOrEmpty(currentHash) && string.Equals(incomingHash, currentHash, StringComparison.Ordinal))
                    {
                        _logger.Info("Legacy migration skipped: incoming mapped configuration is identical to current DB config.");
                        UpdateMetadata(incomingHash);
                    }
                    else
                    {
                        await queueOperation(new ConfigurationOperation
                        {
                            Type = OperationType.SaveConfiguration,
                            Configuration = mapped,
                            DetectChanges = true,
                            ChangeDescription = "Migrated from legacy config",
                            Timestamp = DateTime.UtcNow,
                            SourceId = "migration",
                            SuppressEvents = false
                        });
                        UpdateMetadata(incomingHash);
                        _logger.Info("Queued legacy migration write");
                    }

                    _fileStorage.Delete(legacyFilePath);
                    _logger.Info("Legacy configuration migration completed and source removed");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to migrate legacy configuration");
            }
        }
    }
}