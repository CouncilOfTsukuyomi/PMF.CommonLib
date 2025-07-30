using System.Collections;
using System.Collections.Concurrent;
using System.Threading.Channels;
using AutoMapper;
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
    
    private readonly int _batchSize;
    private readonly int _batchTimeoutMs;
    
    private readonly int _maxHistoryRecords;
    private readonly TimeSpan _historyRetentionPeriod;
    private readonly int _cleanupBatchCounter;
    private int _currentBatchCount = 0;
    
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
            _maxHistoryRecords = 50;
            _historyRetentionPeriod = TimeSpan.FromDays(7);
            _cleanupBatchCounter = 5;
            _logger.Info("Test environment detected - configured for immediate processing");
        }
        else
        {
            _batchSize = 10;
            _batchTimeoutMs = 50;
            _cacheExpiry = TimeSpan.FromMinutes(2);
            _maxHistoryRecords = 1000;
            _historyRetentionPeriod = TimeSpan.FromDays(30);
            _cleanupBatchCounter = 20;
        }
        
        _databasePath = GetDatabasePath(databasePath);

        _logger.Info("ConfigurationService initializing with database path: {DatabasePath}", _databasePath);
        _logger.Info("History cleanup settings - Max records: {MaxRecords}, Retention: {Retention} days, Cleanup frequency: every {Frequency} batches", 
            _maxHistoryRecords, _historyRetentionPeriod.TotalDays, _cleanupBatchCounter);

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
                    
                    _currentBatchCount++;
                    if (_currentBatchCount >= _cleanupBatchCounter)
                    {
                        _logger.Debug("Batch counter reached {Count}, scheduling history cleanup", _cleanupBatchCounter);
                        _ = Task.Run(CleanupHistoryAsync);
                        _currentBatchCount = 0;
                    }
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