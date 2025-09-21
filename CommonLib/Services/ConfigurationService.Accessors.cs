using System.Collections;
using System.Linq;
using CommonLib.Enums;
using CommonLib.Events;
using CommonLib.Helper;
using CommonLib.Models;
using LiteDB;

namespace CommonLib.Services;

public partial class ConfigurationService
{
    public ConfigurationModel GetConfiguration()
    {
        const string cacheKey = "main_config";
        
        if (_configCache.TryGetValue(cacheKey, out var cached) && 
            DateTime.UtcNow - cached.lastUpdated < _cacheExpiry)
        {
            _logger.Debug("Retrieved configuration from cache (age: {Age:F1}s)", 
                (DateTime.UtcNow - cached.lastUpdated).TotalSeconds);
            
            // Add debug info about the cached configuration
            try
            {
                var downloadPath = GetPropertyValue(cached.config, "BackgroundWorker.DownloadPath");
                _logger.Debug("Cache contains - BackgroundWorker.DownloadPath: '{Value}' (Type: {Type})", 
                    downloadPath, downloadPath?.GetType().Name);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to read BackgroundWorker.DownloadPath from cached config");
            }
            
            return cached.config;
        }

        if (!_databaseInitialized)
        {
            _logger.Debug("Database not yet initialized, returning default configuration");
            return new ConfigurationModel();
        }

        _logger.Debug("Cache miss for configuration, querying database");

        try
        {
            return Task.Run(async () =>
            {
                var connectionString = $"Filename={_databasePath};Connection=Shared;Timeout=00:00:05";
                using var database = new LiteDatabase(connectionString);
                
                var configurations = database.GetCollection<ConfigurationRecord>("configurations");
                
                var allRecords = new List<ConfigurationRecord>();
                
                try
                {
                    foreach (var record in configurations.FindAll())
                    {
                        try
                        {
                            var safeRecord = new ConfigurationRecord
                            {
                                Key = ConvertValueSafely(record.Key)?.ToString() ?? string.Empty,
                                Value = ConvertValueSafely(record.Value),
                                ValueType = ConvertValueSafely(record.ValueType)?.ToString() ?? "System.String",
                                LastModified = record.LastModified
                            };
                            allRecords.Add(safeRecord);
                        }
                        catch (Exception recordEx)
                        {
                            _logger.Warn(recordEx, "Failed to process configuration record with key: {Key}, skipping", 
                                record.Key?.ToString() ?? "unknown");
                            continue;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to retrieve configuration records from database, returning default");
                    return new ConfigurationModel();
                }
                
                ConfigurationModel config;
                if (allRecords.Any())
                {
                    try
                    {
                        var flatConfig = allRecords.ToDictionary(
                            r => r.Key, 
                            r => (r.Value, r.ValueType)
                        );
                        
                        config = ConfigurationFlattener.UnflattenConfiguration(flatConfig);
                        _logger.Debug("Retrieved and reconstructed configuration from {Count} database records", allRecords.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Failed to reconstruct configuration from database records, using default");
                        config = new ConfigurationModel();
                    }
                }
                else
                {
                    config = new ConfigurationModel();
                    _logger.Info("No configuration found in database, using default");
                }
                
                _configCache[cacheKey] = (config, DateTime.UtcNow);
                
                // Add debug info about the loaded configuration
                try
                {
                    var downloadPath = GetPropertyValue(config, "BackgroundWorker.DownloadPath");
                    _logger.Debug("Loaded from DB - BackgroundWorker.DownloadPath: '{Value}' (Type: {Type})", 
                        downloadPath, downloadPath?.GetType().Name);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Failed to read BackgroundWorker.DownloadPath from loaded config");
                }
                
                return config;
                
            }).ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to retrieve configuration, returning default");
            return new ConfigurationModel();
        }
    }

    private object ConvertValueSafely(object value)
    {
        if (value == null) return string.Empty;
        
        try
        {
            if (value is Guid guid)
            {
                _logger.Debug("Converting GUID value to string: {Guid}", guid);
                return guid.ToString();
            }
            
            var stringValue = value.ToString();
            return stringValue ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "Failed to convert value safely, using empty string. Value type: {ValueType}", 
                value?.GetType()?.Name ?? "null");
            return string.Empty;
        }
    }

    public void SaveConfiguration(ConfigurationModel updatedConfig, bool detectChangesAndInvokeEvents = true)
    {
        if (updatedConfig == null)
            throw new ArgumentNullException(nameof(updatedConfig));

        var operation = new ConfigurationOperation
        {
            Type = OperationType.SaveConfiguration,
            Configuration = updatedConfig,
            DetectChanges = detectChangesAndInvokeEvents,
            ChangeDescription = detectChangesAndInvokeEvents ? "Configuration updated" : "Configuration saved without change detection",
            Timestamp = DateTime.UtcNow,
            SourceId = "local",
            SuppressEvents = false
        };

        QueueConfigurationOperationFireAndForget(operation);
    }
    
    public void CreateConfiguration()
    {
        try
        {
            var connectionString = $"Filename={_databasePath};Connection=Shared;Timeout=00:00:05";
            using var database = new LiteDatabase(connectionString);
            var configurations = database.GetCollection<ConfigurationRecord>("configurations");
            
            if (configurations.Count() > 0)
            {
                _logger.Info("Configuration already exists, skipping creation");
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "Failed to check existing configuration, proceeding with creation");
        }

        _logger.Info("No existing configuration found, creating default configuration");
    
        var operation = new ConfigurationOperation
        {
            Type = OperationType.CreateConfiguration,
            Configuration = new ConfigurationModel(),
            ChangeDescription = "Default configuration created",
            Timestamp = DateTime.UtcNow,
            SourceId = "local",
            SuppressEvents = false
        };

        QueueConfigurationOperationFireAndForget(operation);
    }

    public void ResetToDefaultConfiguration()
    {
        var operation = new ConfigurationOperation
        {
            Type = OperationType.ResetConfiguration,
            Configuration = new ConfigurationModel(),
            ChangeDescription = "Configuration reset to defaults",
            Timestamp = DateTime.UtcNow,
            SourceId = "local",
            SuppressEvents = false
        };

        QueueConfigurationOperationFireAndForget(operation);
        
        ConfigurationChanged?.Invoke(this, new ConfigurationChangedEventArgs("All", new ConfigurationModel(), "local"));
    }

    public object ReturnConfigValue(Func<ConfigurationModel, object> propertySelector)
    {
        if (propertySelector == null)
            throw new ArgumentNullException(nameof(propertySelector));

        var config = GetConfiguration();
        return propertySelector(config);
    }

    public void UpdateConfigValue(Action<ConfigurationModel> propertyUpdater, string changedPropertyPath, object newValue)
    {
        if (propertyUpdater == null)
            throw new ArgumentNullException(nameof(propertyUpdater));

        var config = GetConfiguration();
        propertyUpdater(config);
        
        ConfigurationChanged?.Invoke(this, new ConfigurationChangedEventArgs(changedPropertyPath, newValue, "local"));
        
        var operation = new ConfigurationOperation
        {
            Type = OperationType.SaveConfiguration,
            Configuration = config,
            DetectChanges = false,
            ChangeDescription = $"Updated {changedPropertyPath}",
            Timestamp = DateTime.UtcNow,
            SourceId = "local",
            SuppressEvents = false
        };

        QueueConfigurationOperationFireAndForget(operation);
    }

    public void UpdateConfigFromExternal(string propertyPath, object newValue, string? sourceId = null)
    {
        _logger.Debug("UpdateConfigFromExternal called - PropertyPath: '{PropertyPath}', ValueType: '{ValueType}', SourceId: '{SourceId}'", 
            propertyPath, newValue?.GetType().Name, sourceId ?? "external");

        var config = GetConfiguration();
        
        var originalValue = GetPropertyValue(config, propertyPath);
        _logger.Debug("Original value before update: '{OriginalValue}' (Type: {OriginalType})", 
            originalValue, originalValue?.GetType().Name);
        
        PropertyUpdater.SetPropertyValue(config, propertyPath, newValue);
        
        var updatedValue = GetPropertyValue(config, propertyPath);
        _logger.Debug("Value after PropertyUpdater.SetPropertyValue: '{UpdatedValue}' (Type: {UpdatedType})", 
            updatedValue, updatedValue?.GetType().Name);
        
        // Immediately update the cache with the modified configuration
        const string cacheKey = "main_config";
        _configCache[cacheKey] = (config, DateTime.UtcNow);
        _logger.Debug("Updated configuration cache immediately with modified config. Cache now contains {Count} items", 
            _configCache.Count);
        
        // Verify the cache update worked
        if (_configCache.TryGetValue(cacheKey, out var cachedConfig))
        {
            var verifyValue = GetPropertyValue(cachedConfig.config, propertyPath);
            _logger.Debug("Cache verification - PropertyPath: '{PropertyPath}', CachedValue: '{CachedValue}' (Type: {CachedType})", 
                propertyPath, verifyValue, verifyValue?.GetType().Name);
        }
        
        var operation = new ConfigurationOperation
        {
            Type = OperationType.SaveConfiguration,
            Configuration = config,
            DetectChanges = false,
            ChangeDescription = $"External update to {propertyPath}",
            Timestamp = DateTime.UtcNow,
            SourceId = sourceId ?? "external",
            SuppressEvents = false,
            TargetKeys = new List<string> { propertyPath }
        };

        _logger.Debug("Queueing configuration operation with DetectChanges=false and TargetKeys override");
        QueueConfigurationOperationFireAndForget(operation);
    }

    private object GetPropertyValue(object obj, string propertyPath)
    {
        try
        {
            var properties = propertyPath.Split('.');
            object currentObject = obj;

            foreach (var propertyName in properties)
            {
                var propertyInfo = currentObject.GetType().GetProperty(propertyName);
                if (propertyInfo == null)
                    return null;
                
                currentObject = propertyInfo.GetValue(currentObject);
                if (currentObject == null)
                    return null;
            }

            return currentObject;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to get property value for path: {PropertyPath}", propertyPath);
            return null;
        }
    }
}
