
using System.Collections;
using System.Collections.Concurrent;
using System.Threading.Channels;
using AutoMapper;
using CommonLib.Consts;
using CommonLib.Enums;
using CommonLib.Events;
using CommonLib.Helper;
using CommonLib.Interfaces;
using CommonLib.Models;
using LiteDB;
using Newtonsoft.Json;
using NLog;

namespace CommonLib.Services;

public class ConfigurationService : IConfigurationService, IDisposable
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    private readonly string _databasePath;
    private readonly IMapper _mapper;
    private readonly IFileStorage _fileStorage;
    private readonly IConfigurationMigrator _migrator;
    private readonly IConfigurationChangeDetector _changeDetector;
    
    private static readonly SemaphoreSlim _globalDbSemaphore = new(1, 1);
    private static readonly SemaphoreSlim _migrationSemaphore = new(1, 1);
    
    private readonly Channel<ConfigurationOperation> _operationChannel;
    private readonly ChannelWriter<ConfigurationOperation> _operationWriter;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task _backgroundProcessor;
    
    private readonly ConcurrentDictionary<string, (ConfigurationModel config, DateTime lastUpdated)> _configCache = new();
    private readonly TimeSpan _cacheExpiry;
    
    private readonly int _maxHistoryRecords;
    private readonly TimeSpan _historyRetentionPeriod;
    private readonly int _cleanupOperationCounter;
    private int _currentOperationCount = 0;
    
    private volatile bool _databaseInitialized = false;
    private static volatile bool _migrationCompleted = false;
    private readonly object _initLock = new object();
    
    private bool _disposed = false;

    private readonly ConcurrentQueue<ConfigurationOperation> _operationQueue = new();

    public event EventHandler<ConfigurationChangedEventArgs>? ConfigurationChanged;

    public ConfigurationService(IFileStorage fileStorage, IMapper mapper, string? databasePath = null)
    {
        _fileStorage = fileStorage;
        _mapper = mapper;
        _migrator = new ConfigurationMigrator(_fileStorage, _mapper, _logger);
        _changeDetector = new ConfigurationChangeDetector(_logger);
        
        var isTestEnvironment = EnvironmentDetector.IsRunningInTestEnvironment();
        
        if (isTestEnvironment)
        {
            _cacheExpiry = TimeSpan.FromSeconds(1);
            _maxHistoryRecords = 50;
            _historyRetentionPeriod = TimeSpan.FromDays(7);
            _cleanupOperationCounter = 5;
            _logger.Info("Test environment detected - configured for immediate processing");
        }
        else
        {
            _cacheExpiry = TimeSpan.FromMinutes(2);
            _maxHistoryRecords = 1000;
            _historyRetentionPeriod = TimeSpan.FromDays(30);
            _cleanupOperationCounter = 20;
        }
        
        _databasePath = GetDatabasePath(databasePath);

        _logger.Info("ConfigurationService initializing with database path: {DatabasePath}", _databasePath);
        _logger.Info("History cleanup settings - Max records: {MaxRecords}, Retention: {Retention} days, Cleanup frequency: every {Frequency} operations", 
            _maxHistoryRecords, _historyRetentionPeriod.TotalDays, _cleanupOperationCounter);

        var options = new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };
        
        _operationChannel = Channel.CreateBounded<ConfigurationOperation>(options);
        _operationWriter = _operationChannel.Writer;

        Task.Run(async () =>
        {
            try
            {
                await InitializeDatabaseAsync();
                lock (_initLock)
                {
                    _databaseInitialized = true;
                }
                _logger.Info("Database initialization completed successfully");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Database initialization failed");
            }
        });
        
        _backgroundProcessor = Task.Run(ProcessOperationsAsync, _cancellationTokenSource.Token);
        _logger.Info("ConfigurationService background processor started");
    }

    private string GetDatabasePath(string? providedPath)
    {
        if (!string.IsNullOrEmpty(providedPath))
        {
            return providedPath;
        }

        #if DEBUG
        var debugPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
                                      "Atomos", "Debug", "configuration.db");
        
        var debugDir = Path.GetDirectoryName(debugPath);
        if (!Directory.Exists(debugDir))
        {
            Directory.CreateDirectory(debugDir);
            _logger.Info("Created debug directory: {DebugDir}", debugDir);
        }
        
        _logger.Info("Debug mode detected - using debug database path");
        return debugPath;
        #else
        var configDir = Path.GetDirectoryName(ConfigurationConsts.ConfigurationFilePath);
        return Path.Combine(configDir, "configuration.db");
        #endif
    }

    private async Task InitializeDatabaseAsync()
    {
        _logger.Info("Initializing configuration database at: {DatabasePath}", _databasePath);
        
        var directoryPath = Path.GetDirectoryName(_databasePath);
        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
            _logger.Info("Created missing directory for database at '{DirectoryPath}'", directoryPath);
        }

        await Task.Run(() =>
        {
            try
            {
                var connectionString = $"Filename={_databasePath};Connection=Shared;Timeout=00:00:10";
                using var database = new LiteDatabase(connectionString);
                
                var configurations = database.GetCollection<ConfigurationRecord>("configurations");
                configurations.EnsureIndex(x => x.Key, true);
                configurations.EnsureIndex(x => x.LastModifiedTicks);
                
                var configHistory = database.GetCollection<ConfigurationHistoryRecord>("configuration_history");
                configHistory.EnsureIndex(x => x.Key);
                configHistory.EnsureIndex(x => x.ModifiedDateTicks);
                
                _logger.Info("Configuration database initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to initialize database");
                throw;
            }
        });
        
        if (!_migrationCompleted)
        {
            await PerformMigrationWithLocking();
        }
        else
        {
            _logger.Info("Migration already completed by another process, skipping");
        }
    }

    private async Task PerformMigrationWithLocking()
    {
        const int maxRetries = 5;
        const int baseDelayMs = 1000;
        
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await _migrationSemaphore.WaitAsync(_cancellationTokenSource.Token);
                
                try
                {
                    if (_migrationCompleted)
                    {
                        _logger.Info("Migration completed by another process while waiting");
                        return;
                    }
                    
                    _logger.Info("Starting migration attempt {Attempt}", attempt);
                    await _migrator.MigrateAsync(_databasePath, QueueConfigurationOperationAsync);
                    
                    _migrationCompleted = true;
                    _logger.Info("Migration completed successfully");
                    return;
                }
                finally
                {
                    _migrationSemaphore.Release();
                }
            }
            catch (IOException ioEx) when (ioEx.Message.Contains("being used by another process") && attempt < maxRetries)
            {
                var delayMs = baseDelayMs * attempt;
                _logger.Warn("Migration failed due to file being in use (attempt {Attempt}/{MaxRetries}), retrying in {Delay}ms", 
                    attempt, maxRetries, delayMs);
                
                await Task.Delay(delayMs, _cancellationTokenSource.Token);
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                var delayMs = baseDelayMs * attempt;
                _logger.Warn(ex, "Migration failed (attempt {Attempt}/{MaxRetries}), retrying in {Delay}ms", 
                    attempt, maxRetries, delayMs);
                
                await Task.Delay(delayMs, _cancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Migration failed after {Attempt} attempts, continuing without migration", attempt);
                return;
            }
        }
    }

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
            DetectChanges = true,
            ChangeDescription = $"External update to {propertyPath}",
            Timestamp = DateTime.UtcNow,
            SourceId = sourceId ?? "external",
            SuppressEvents = false
        };

        _logger.Debug("Queueing configuration operation with DetectChanges=true and SuppressEvents=false");
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

    public async Task FlushPendingChangesAsync()
    {
        _logger.Info("Flushing pending configuration changes... Current queue size: {QueueSize}", _operationQueue.Count);
        
        if (_operationQueue.IsEmpty)
        {
            _logger.Info("No pending configuration changes to flush");
            return;
        }
        
        var timeout = TimeSpan.FromSeconds(15);
        var startTime = DateTime.UtcNow;
        var initialCount = _operationQueue.Count;
        
        while (!_operationQueue.IsEmpty && DateTime.UtcNow - startTime < timeout)
        {
            await Task.Delay(100);
            
            if ((DateTime.UtcNow - startTime).TotalSeconds % 2 < 0.1)
            {
                _logger.Debug("Still flushing... {Remaining} operations remaining", _operationQueue.Count);
            }
        }
        
        var finalCount = _operationQueue.Count;
        var processedCount = initialCount - finalCount;
        
        if (finalCount > 0)
        {
            _logger.Warn("Timeout waiting for configuration operations to complete after {Timeout}s. " +
                        "Processed: {Processed}, Remaining: {Remaining}", 
                        timeout.TotalSeconds, processedCount, finalCount);
        }
        else
        {
            _logger.Info("All {Count} pending configuration changes flushed successfully in {Duration:F1}s", 
                        processedCount, (DateTime.UtcNow - startTime).TotalSeconds);
        }
    }

    public void FlushPendingChangesSync()
    {
        try
        {
            var task = FlushPendingChangesAsync();
            task.Wait(TimeSpan.FromSeconds(15));
            
            if (!task.IsCompletedSuccessfully)
            {
                _logger.Warn("Synchronous flush did not complete successfully. Task status: {Status}", task.Status);
            }
        }
        catch (AggregateException ex)
        {
            _logger.Error(ex.Flatten(), "Error during synchronous configuration flush");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error during synchronous configuration flush");
        }
    }

    public int GetPendingOperationCount()
    {
        return _operationQueue.Count;
    }

    private async Task QueueConfigurationOperationAsync(ConfigurationOperation operation)
    {
        try
        {
            _logger.Debug("Attempting to queue configuration operation: {Type}", operation.Type);
            
            _operationQueue.Enqueue(operation);

            if (!_operationWriter.TryWrite(operation))
            {
                _logger.Debug("TryWrite failed for operation {Type}, using WriteAsync", operation.Type);
                await _operationWriter.WriteAsync(operation, _cancellationTokenSource.Token);
                _logger.Debug("WriteAsync completed for operation {Type}", operation.Type);
            }
            else
            {
                _logger.Debug("Successfully queued operation {Type} via TryWrite", operation.Type);
            }
        }
        catch (Exception ex)
        {
            if (_operationQueue.TryDequeue(out _))
            {
                _logger.Debug("Removed failed operation from tracking queue");
            }
            _logger.Error(ex, "Failed to queue configuration operation: {Type}", operation.Type);
        }
    }

    private void QueueConfigurationOperationFireAndForget(ConfigurationOperation operation)
    {
        Task.Run(async () =>
        {
            await QueueConfigurationOperationAsync(operation);
        });
    }

    private async Task ProcessOperationsAsync()
    {
        _logger.Info("Configuration background processor started - waiting for database initialization");
        
        while (!_databaseInitialized && !_cancellationTokenSource.Token.IsCancellationRequested)
        {
            await Task.Delay(100, _cancellationTokenSource.Token);
        }
        
        if (_cancellationTokenSource.Token.IsCancellationRequested)
        {
            _logger.Info("Background processor cancelled before database initialization");
            return;
        }
        
        _logger.Info("Database initialized, configuration background processor now active");
        
        var reader = _operationChannel.Reader;
        
        try
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                var hasMore = await reader.WaitToReadAsync(_cancellationTokenSource.Token);
                if (!hasMore) break;
            
                while (reader.TryRead(out var operation))
                {
                    _logger.Debug("Received configuration operation: {Type} at {Timestamp} from source: {SourceId}", 
                        operation.Type, operation.Timestamp, operation.SourceId ?? "unknown");
                
                    await ProcessConfigurationOperationAsync(operation);
                    
                    _currentOperationCount++;
                    if (_currentOperationCount >= _cleanupOperationCounter)
                    {
                        _logger.Debug("Operation counter reached {Count}, scheduling history cleanup", _cleanupOperationCounter);
                        _ = Task.Run(CleanupHistoryAsync);
                        _currentOperationCount = 0;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Info("Configuration background processor cancelled");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Critical error in configuration background processor");
        }
        finally
        {
            _logger.Info("Configuration background processor ended");
        }
    }

    private async Task ProcessConfigurationOperationAsync(ConfigurationOperation operation)
    {
        await Task.Run(() =>
        {
            try
            {
                _logger.Debug("Processing configuration operation: {Type} from source: {SourceId}", 
                    operation.Type, operation.SourceId ?? "unknown");

                var connectionString = $"Filename={_databasePath};Connection=Shared;Timeout=00:00:10";
                using var database = new LiteDatabase(connectionString);
                
                var configurations = database.GetCollection<ConfigurationRecord>("configurations");
                var configHistory = database.GetCollection<ConfigurationHistoryRecord>("configuration_history");
        
                ConfigurationModel originalConfig = null;
        
                if (operation.DetectChanges)
                {
                    var currentRecords = configurations.FindAll().ToList();
                    if (currentRecords.Any())
                    {
                        var currentFlat = currentRecords.ToDictionary(r => r.Key, r => (r.Value, r.ValueType));
                        originalConfig = ConfigurationFlattener.UnflattenConfiguration(currentFlat);
                        _logger.Debug("Loaded original config for change detection with {Count} records", currentRecords.Count);
                    }
                    else
                    {
                        _logger.Debug("No existing configuration found for change detection");
                    }
                }
        
                var flatConfig = ConfigurationFlattener.FlattenConfiguration(operation.Configuration);
                _logger.Debug("Flattened configuration to {Count} key-value pairs", flatConfig.Count);
                
                var existingRecords = configurations.FindAll().ToDictionary(r => r.Key, r => r);
                var keysToRemove = existingRecords.Keys.Except(flatConfig.Keys).ToList();
                
                if (keysToRemove.Any())
                {
                    _logger.Debug("Removing {Count} obsolete configuration keys", keysToRemove.Count);
                    foreach (var keyToRemove in keysToRemove)
                    {
                        configurations.Delete(keyToRemove);
                    }
                }
                
                var updatedCount = 0;
                foreach (var kvp in flatConfig)
                {
                    var configRecord = new ConfigurationRecord
                    {
                        Key = kvp.Key,
                        Value = kvp.Value.value,
                        ValueType = kvp.Value.type,
                        LastModified = operation.Timestamp
                    };
                    
                    configurations.Upsert(configRecord);
                    updatedCount++;
                    
                    var historyRecord = new ConfigurationHistoryRecord
                    {
                        Key = kvp.Key,
                        Value = kvp.Value.value,
                        ValueType = kvp.Value.type,
                        ModifiedDate = operation.Timestamp,
                        ChangeDescription = operation.ChangeDescription,
                        SourceId = operation.SourceId
                    };

                    configHistory.Insert(historyRecord);
                }
                
                _logger.Debug("Updated {Count} configuration records in database", updatedCount);
        
                _configCache.Clear();
                _logger.Debug("Cleared configuration cache");
                
                var eventsRaised = 0;
                if (operation.DetectChanges && !operation.SuppressEvents)
                {
                    if (originalConfig != null)
                    {
                        var changes = _changeDetector.GetChanges(originalConfig, operation.Configuration);
                        _logger.Debug("Detected {Count} changes for operation from source {SourceId}", 
                            changes.Count, operation.SourceId);
                        
                        foreach (var change in changes)
                        {
                            _logger.Debug("Raising ConfigurationChanged event for '{PropertyPath}' = '{Value}' from source '{SourceId}'", 
                                change.Key, change.Value, operation.SourceId);
                            
                            ConfigurationChanged?.Invoke(
                                this,
                                new ConfigurationChangedEventArgs(change.Key, change.Value, operation.SourceId)
                            );
                            eventsRaised++;
                        }
                    }
                    else
                    {
                        _logger.Debug("No original config available for change detection");
                    }
                }
                
                _logger.Debug("Raised {Count} configuration change events", eventsRaised);
        
                _logger.Info("Successfully processed configuration operation {Type} with {SettingCount} settings from source {SourceId}", 
                    operation.Type, flatConfig.Count, operation.SourceId ?? "unknown");
                
                _operationQueue.TryDequeue(out _);
                _logger.Debug("Removed processed operation from tracking queue. Remaining: {Remaining}", _operationQueue.Count);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to process configuration operation: {Type} from source: {SourceId}", 
                    operation.Type, operation.SourceId ?? "unknown");
            }
        });
    }

    private async Task CleanupHistoryAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                var connectionString = $"Filename={_databasePath};Connection=Shared;Timeout=00:00:10";
                using var database = new LiteDatabase(connectionString);
                
                var configHistory = database.GetCollection<ConfigurationHistoryRecord>("configuration_history");
                
                var totalRecords = configHistory.Count();
                
                if (totalRecords <= _maxHistoryRecords)
                {
                    _logger.Debug("History cleanup skipped - current records ({Count}) below limit ({Limit})", 
                        totalRecords, _maxHistoryRecords);
                    return;
                }
                
                _logger.Info("Starting history cleanup - current records: {Count}, limit: {Limit}", 
                    totalRecords, _maxHistoryRecords);
                
                var cutoffDate = DateTime.UtcNow.Subtract(_historyRetentionPeriod);
                var cutoffTicks = cutoffDate.Ticks;
                
                var expiredRecords = configHistory.Find(x => x.ModifiedDateTicks < cutoffTicks);
                var expiredCount = 0;
                
                foreach (var record in expiredRecords)
                {
                    configHistory.Delete(record.Id);
                    expiredCount++;
                }
                
                _logger.Info("Removed {Count} expired history records (older than {Date})", 
                    expiredCount, cutoffDate.ToString("yyyy-MM-dd"));
                
                var remainingRecords = configHistory.Count();
                if (remainingRecords > _maxHistoryRecords)
                {
                    var excessCount = remainingRecords - _maxHistoryRecords;
                    
                    var oldestRecords = configHistory.Find(Query.All("ModifiedDateTicks", Query.Ascending), 0, excessCount);
                    
                    var removedCount = 0;
                    foreach (var record in oldestRecords)
                    {
                        configHistory.Delete(record.Id);
                        removedCount++;
                    }
                    
                    _logger.Info("Removed {Count} oldest history records to maintain limit of {Limit}", 
                        removedCount, _maxHistoryRecords);
                }
                
                var finalCount = configHistory.Count();
                var totalRemoved = totalRecords - finalCount;
                
                _logger.Info("History cleanup completed - removed {Removed} records, {Remaining} remaining", 
                    totalRemoved, finalCount);
                
                database.Rebuild();
                _logger.Debug("Database rebuilt to reclaim space after history cleanup");
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to cleanup configuration history");
        }
    }
    
    public async Task<string> ExportToFileAsync(string? filePath = null)
    {
        if (!_databaseInitialized)
        {
            _logger.Warn("Database not yet initialized, cannot export configuration");
            throw new InvalidOperationException("Database not yet initialized");
        }

        if (string.IsNullOrEmpty(filePath))
        {
            var configDir = Path.GetDirectoryName(_databasePath);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            filePath = Path.Combine(configDir, $"configuration_export_{timestamp}.json");
        }

        _logger.Info("Starting configuration export to file: {FilePath}", filePath);

        try
        {
            var json = await Task.Run(() =>
            {
                var connectionString = $"Filename={_databasePath};Connection=Shared;Timeout=00:00:10";
                using var database = new LiteDatabase(connectionString);

                var configurations = database.GetCollection<ConfigurationRecord>("configurations");
                var allRecords = configurations.FindAll().OrderBy(r => r.Key).ToList();

                if (!allRecords.Any())
                {
                    _logger.Info("No configuration records found in database");
                    return "{}";
                }
                
                var flatConfig = allRecords.ToDictionary(
                    r => r.Key, 
                    r => (r.Value, r.ValueType)
                );
                
                var reconstructedConfig = ConfigurationFlattener.UnflattenConfiguration(flatConfig);
                
                var currentConfig = GetConfiguration();
                var verificationResult = VerifyConfigurationMatch(currentConfig, reconstructedConfig);

                var export = new
                {
                    ExportInfo = new
                    {
                        ExportedAt = DateTime.UtcNow,
                        RecordCount = allRecords.Count,
                        FilePath = filePath,
                        VerificationPassed = verificationResult.IsMatch,
                        VerificationDetails = verificationResult.Details
                    },
                    Configuration = reconstructedConfig
                };

                return JsonConvert.SerializeObject(export, Formatting.Indented);
            });

            var directory = Path.GetDirectoryName(filePath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(filePath, json);

            _logger.Info("Configuration export completed successfully to {FilePath}, file size: {Size} bytes", 
                filePath, new FileInfo(filePath).Length);
            
            return filePath;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to export configuration to file: {FilePath}", filePath);
            throw;
        }
    }
    
    private (bool IsMatch, List<string> Details) VerifyConfigurationMatch(ConfigurationModel original, ConfigurationModel exported)
    {
        var details = new List<string>();
        var isMatch = true;

        try
        {
            var originalFlat = ConfigurationFlattener.FlattenConfiguration(original);
            var exportedFlat = ConfigurationFlattener.FlattenConfiguration(exported);
            
            var missingInExport = originalFlat.Keys.Except(exportedFlat.Keys).ToList();
            if (missingInExport.Any())
            {
                isMatch = false;
                details.Add($"Missing keys in export: {string.Join(", ", missingInExport)}");
            }
            
            var extraInExport = exportedFlat.Keys.Except(originalFlat.Keys).ToList();
            if (extraInExport.Any())
            {
                isMatch = false;
                details.Add($"Extra keys in export: {string.Join(", ", extraInExport)}");
            }
            
            var valueMismatches = new List<string>();
            foreach (var key in originalFlat.Keys.Intersect(exportedFlat.Keys))
            {
                var originalValue = originalFlat[key];
                var exportedValue = exportedFlat[key];

                if (!AreValuesEqual(originalValue.value, exportedValue.value) || 
                    originalValue.type != exportedValue.type)
                {
                    var originalValueStr = GetValueDisplayString(originalValue.value);
                    var exportedValueStr = GetValueDisplayString(exportedValue.value);
                    
                    valueMismatches.Add($"{key}: original='{originalValueStr}' ({originalValue.type}) vs exported='{exportedValueStr}' ({exportedValue.type})");
                }
            }

            if (valueMismatches.Any())
            {
                isMatch = false;
                details.Add($"Value mismatches: {string.Join("; ", valueMismatches)}");
            }

            if (isMatch)
            {
                details.Add($"All {originalFlat.Count} configuration keys match perfectly");
            }

            _logger.Debug("Configuration verification completed - Match: {IsMatch}, Keys checked: {Count}", 
                isMatch, originalFlat.Count);

            return (isMatch, details);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error during configuration verification");
            return (false, new List<string> { $"Verification error: {ex.Message}" });
        }
    }

    private string GetValueDisplayString(object value)
    {
        if (value == null) return "null";
        
        if (value is IEnumerable enumerable && !(value is string))
        {
            var items = enumerable.Cast<object>().Select(x => x?.ToString() ?? "null");
            return $"[{string.Join(", ", items)}]";
        }
        
        return value.ToString() ?? "null";
    }

    private bool AreValuesEqual(object value1, object value2)
    {
        if (value1 == null && value2 == null) return true;
        if (value1 == null || value2 == null) return false;
        
        if (value1 is IEnumerable enum1 && value2 is IEnumerable enum2 && 
            !(value1 is string) && !(value2 is string))
        {
            return AreCollectionsEqual(enum1, enum2);
        }
    
        if (IsNumericType(value1) && IsNumericType(value2))
        {
            try
            {
                var decimal1 = Convert.ToDecimal(value1);
                var decimal2 = Convert.ToDecimal(value2);
                return decimal1 == decimal2;
            }
            catch
            {
                return false;
            }
        }
    
        return value1.Equals(value2);
    }

    private bool AreCollectionsEqual(IEnumerable collection1, IEnumerable collection2)
    {
        var list1 = collection1.Cast<object>().ToList();
        var list2 = collection2.Cast<object>().ToList();
    
        if (list1.Count != list2.Count)
            return false;
    
        for (int i = 0; i < list1.Count; i++)
        {
            if (!AreValuesEqual(list1[i], list2[i]))
                return false;
        }
    
        return true;
    }

    private bool IsNumericType(object value)
    {
        return value is sbyte || value is byte || value is short || value is ushort ||
               value is int || value is uint || value is long || value is ulong ||
               value is float || value is double || value is decimal;
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _logger.Info("ConfigurationService disposal starting - flushing pending changes");
        _disposed = true;
        
        try
        {
            FlushPendingChangesSync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error flushing pending changes during disposal");
        }
        
        _operationWriter.Complete();
        _cancellationTokenSource.Cancel();
        
        try
        {
            _backgroundProcessor.Wait(TimeSpan.FromSeconds(5));
            _logger.Info("Configuration background processor completed gracefully");
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "Configuration background processor did not complete gracefully");
        }
        
        _cancellationTokenSource?.Dispose();
        
        _logger.Info("ConfigurationService disposed successfully");
    }
}