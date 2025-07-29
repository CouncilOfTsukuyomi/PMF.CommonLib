using AutoMapper;
using CommonLib.Consts;
using CommonLib.Enums;
using CommonLib.Interfaces;
using CommonLib.Models;
using Newtonsoft.Json;
using NLog;

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
        var legacyFilePath = $"{ConfigurationConsts.OldConfigPath}\\config.json";
        var newFilePath = ConfigurationConsts.ConfigurationFilePath;
        
        if (_fileStorage.Exists(newFilePath))
        {
            try
            {
                _logger.Info("Migrating existing config file to database...");
                
                using var stream = _fileStorage.OpenRead(newFilePath);
                using var reader = new StreamReader(stream);
                var fileContent = reader.ReadToEnd();
                
                var fileConfig = JsonConvert.DeserializeObject<ConfigurationModel>(fileContent);
                
                if (fileConfig != null)
                {
                    await queueOperation(new ConfigurationOperation
                    {
                        Type = OperationType.SaveConfiguration,
                        Configuration = fileConfig,
                        ChangeDescription = "Migrated from config file",
                        Timestamp = DateTime.UtcNow
                    });
                    
                    var backupPath = newFilePath + ".backup";
                    if (_fileStorage.Exists(backupPath))
                        _fileStorage.Delete(backupPath);
                    
                    var tempName = newFilePath + ".temp";
                    using (var sourceStream = _fileStorage.OpenRead(newFilePath))
                    using (var destStream = _fileStorage.OpenWrite(backupPath))
                    {
                        sourceStream.CopyTo(destStream);
                    }
                    _fileStorage.Delete(newFilePath);
                    
                    _logger.Info("Migration completed. Original file backed up to {BackupPath}", backupPath);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to migrate config file to database");
            }
        }
        
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
                    var newConfig = _mapper.Map<ConfigurationModel>(oldConfig);
                    
                    await queueOperation(new ConfigurationOperation
                    {
                        Type = OperationType.SaveConfiguration,
                        Configuration = newConfig,
                        ChangeDescription = "Migrated from legacy config",
                        Timestamp = DateTime.UtcNow
                    });
                    
                    _fileStorage.Delete(legacyFilePath);
                    _logger.Info("Legacy configuration migrated successfully");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to migrate legacy configuration");
            }
        }
    }
}