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
    
    private readonly int _batchSize;
    private readonly int _batchTimeoutMs;
    
    private volatile bool _databaseInitialized = false;
    private static volatile bool _migrationCompleted = false;
    private readonly object _initLock = new object();
    
    private bool _disposed = false;

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
            _batchSize = 1;
            _batchTimeoutMs = 0;
            _cacheExpiry = TimeSpan.FromSeconds(1);
            _logger.Info("Test environment detected - configured for immediate processing");
        }
        else
        {
            _batchSize = 10;
            _batchTimeoutMs = 50;
            _cacheExpiry = TimeSpan.FromMinutes(2);
        }
        
        if (!string.IsNullOrEmpty(databasePath))
        {
            _databasePath = databasePath;
        }
        else
        {
            var configDir = Path.GetDirectoryName(ConfigurationConsts.ConfigurationFilePath);
            _databasePath = Path.Combine(configDir, "configuration.db");
        }

        _logger.Info("ConfigurationService initializing with database path: {DatabasePath}", _databasePath);

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
            _logger.Debug("Retrieved configuration from cache");
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
                var allRecords = configurations.FindAll().ToList();
                
                ConfigurationModel config;
                if (allRecords.Any())
                {
                    var flatConfig = allRecords.ToDictionary(
                        r => r.Key, 
                        r => (r.Value, r.ValueType)
                    );
                    
                    config = ConfigurationFlattener.UnflattenConfiguration(flatConfig);
                    _logger.Debug("Retrieved and reconstructed configuration from {Count} database records", allRecords.Count);
                }
                else
                {
                    config = new ConfigurationModel();
                    _logger.Info("No configuration found in database, using default");
                }
                
                _configCache[cacheKey] = (config, DateTime.UtcNow);
                return config;
                
            }).ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to retrieve configuration, returning default");
            return new ConfigurationModel();
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
            Timestamp = DateTime.UtcNow
        };

        QueueConfigurationOperationFireAndForget(operation);
    }

    public void CreateConfiguration()
    {
        var operation = new ConfigurationOperation
        {
            Type = OperationType.CreateConfiguration,
            Configuration = new ConfigurationModel(),
            ChangeDescription = "Default configuration created",
            Timestamp = DateTime.UtcNow
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
            Timestamp = DateTime.UtcNow
        };

        QueueConfigurationOperationFireAndForget(operation);
        
        ConfigurationChanged?.Invoke(this, new ConfigurationChangedEventArgs("All", new ConfigurationModel()));
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
        
        ConfigurationChanged?.Invoke(this, new ConfigurationChangedEventArgs(changedPropertyPath, newValue));
        
        var operation = new ConfigurationOperation
        {
            Type = OperationType.SaveConfiguration,
            Configuration = config,
            DetectChanges = false,
            ChangeDescription = $"Updated {changedPropertyPath}",
            Timestamp = DateTime.UtcNow
        };

        QueueConfigurationOperationFireAndForget(operation);
    }

    public void UpdateConfigFromExternal(string propertyPath, object newValue)
    {
        var config = GetConfiguration();
        PropertyUpdater.SetPropertyValue(config, propertyPath, newValue);
        
        ConfigurationChanged?.Invoke(this, new ConfigurationChangedEventArgs(propertyPath, newValue));
        
        var operation = new ConfigurationOperation
        {
            Type = OperationType.SaveConfiguration,
            Configuration = config,
            DetectChanges = false,
            ChangeDescription = $"External update to {propertyPath}",
            Timestamp = DateTime.UtcNow
        };

        QueueConfigurationOperationFireAndForget(operation);
    }

    private async Task QueueConfigurationOperationAsync(ConfigurationOperation operation)
    {
        try
        {
            _logger.Debug("Attempting to queue configuration operation: {Type}", operation.Type);

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
        
        var operations = new List<ConfigurationOperation>();
        var reader = _operationChannel.Reader;
        
        try
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                var hasMore = await reader.WaitToReadAsync(_cancellationTokenSource.Token);
                if (!hasMore) break;
            
                while (reader.TryRead(out var operation))
                {
                    _logger.Debug("Received configuration operation: {Type} at {Timestamp}", 
                        operation.Type, operation.Timestamp);
                
                    operations.Add(operation);
                }
            
                if (operations.Count == 0) continue;
            
                var timeSinceFirstOperation = (DateTime.UtcNow - operations[0].Timestamp).TotalMilliseconds;
            
                if (operations.Count >= _batchSize || timeSinceFirstOperation >= _batchTimeoutMs)
                {
                    _logger.Info("Processing configuration batch of {Count} operations", operations.Count);
                
                    await ProcessConfigurationBatchAsync(operations);
                    operations.Clear();
                }
                else
                {
                    var remainingTimeout = _batchTimeoutMs - (int)timeSinceFirstOperation;
                    if (remainingTimeout > 0)
                    {
                        await Task.Delay(remainingTimeout, _cancellationTokenSource.Token);
                    }
                }
            }
        
            if (operations.Count > 0)
            {
                _logger.Info("Processing final configuration batch of {Count} operations on shutdown", operations.Count);
                await ProcessConfigurationBatchAsync(operations);
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

    private async Task ProcessConfigurationBatchAsync(List<ConfigurationOperation> operations)
    {
        if (operations.Count == 0) return;

        await Task.Run(() =>
        {
            try
            {
                var connectionString = $"Filename={_databasePath};Connection=Shared;Timeout=00:00:10";
                using var database = new LiteDatabase(connectionString);
                
                var configurations = database.GetCollection<ConfigurationRecord>("configurations");
                var configHistory = database.GetCollection<ConfigurationHistoryRecord>("configuration_history");
        
                var latestOperation = operations.OrderByDescending(o => o.Timestamp).First();
        
                ConfigurationModel originalConfig = null;
        
                if (latestOperation.DetectChanges)
                {
                    var currentRecords = configurations.FindAll().ToList();
                    if (currentRecords.Any())
                    {
                        var currentFlat = currentRecords.ToDictionary(r => r.Key, r => (r.Value, r.ValueType));
                        originalConfig = ConfigurationFlattener.UnflattenConfiguration(currentFlat);
                    }
                }
        
                var flatConfig = ConfigurationFlattener.FlattenConfiguration(latestOperation.Configuration);
                
                var existingRecords = configurations.FindAll().ToDictionary(r => r.Key, r => r);
                var keysToRemove = existingRecords.Keys.Except(flatConfig.Keys).ToList();
                
                foreach (var keyToRemove in keysToRemove)
                {
                    configurations.Delete(keyToRemove);
                }
                
                foreach (var kvp in flatConfig)
                {
                    if (existingRecords.TryGetValue(kvp.Key, out var existingRecord))
                    {
                        existingRecord.Value = kvp.Value.value;
                        existingRecord.ValueType = kvp.Value.type;
                        existingRecord.LastModified = latestOperation.Timestamp;
                        
                        configurations.Update(existingRecord);
                    }
                    else
                    {
                        var newRecord = new ConfigurationRecord
                        {
                            Key = kvp.Key,
                            Value = kvp.Value.value,
                            ValueType = kvp.Value.type,
                            LastModified = latestOperation.Timestamp
                        };
                        
                        configurations.Insert(newRecord);
                    }
                    
                    var historyRecord = new ConfigurationHistoryRecord
                    {
                        Key = kvp.Key,
                        Value = kvp.Value.value,
                        ValueType = kvp.Value.type,
                        ModifiedDate = latestOperation.Timestamp,
                        ChangeDescription = latestOperation.ChangeDescription
                    };
                    
                    configHistory.Insert(historyRecord);
                }
        
                _configCache.Clear();
        
                foreach (var operation in operations.Where(o => o.DetectChanges))
                {
                    if (originalConfig != null)
                    {
                        var changes = _changeDetector.GetChanges(originalConfig, operation.Configuration);
                        foreach (var change in changes)
                        {
                            ConfigurationChanged?.Invoke(
                                this,
                                new ConfigurationChangedEventArgs(change.Key, change.Value)
                            );
                        }
                    }
                }
        
                _logger.Debug("Processed configuration batch with {Count} operations, applied latest with {SettingCount} settings", 
                    operations.Count, flatConfig.Count);
        
                _logger.Info("Successfully processed configuration batch of {Count} operations", operations.Count);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to process configuration batch operations");
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _logger.Info("ConfigurationService disposal starting");
        _disposed = true;
        
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