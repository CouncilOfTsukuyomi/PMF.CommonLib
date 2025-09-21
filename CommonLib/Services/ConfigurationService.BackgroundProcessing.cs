using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommonLib.Enums;
using CommonLib.Events;
using CommonLib.Helper;
using CommonLib.Models;
using LiteDB;

namespace CommonLib.Services;

public partial class ConfigurationService
{
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

                // Cross-process coordination: serialize DB writes across processes
                using var globalMutex = new Mutex(false, GlobalDbMutexName);
                var gotMutex = globalMutex.WaitOne(TimeSpan.FromSeconds(15));
                if (!gotMutex)
                {
                    throw new IOException("Timed out waiting for global DB mutex during config write");
                }

                try
                {
                    var connectionString = $"Filename={_databasePath};Connection=Shared;Timeout=00:00:10";
                    using var database = new LiteDatabase(connectionString);
                    
                    var configurations = database.GetCollection<ConfigurationRecord>("configurations");
                    var configHistory = database.GetCollection<ConfigurationHistoryRecord>("configuration_history");
            
                    ConfigurationModel originalConfig = null;
                    List<ConfigurationRecord> currentRecordsForChange = null;
            
                    // Load current records first for merging and optional change detection
                    var existingRecords = configurations.FindAll().ToDictionary(r => r.Key, r => r);
            
                    if (operation.DetectChanges)
                    {
                        currentRecordsForChange = existingRecords.Values.ToList();
                        if (currentRecordsForChange.Any())
                        {
                            var currentFlat = currentRecordsForChange.ToDictionary(r => r.Key, r => (r.Value, r.ValueType));
                            originalConfig = ConfigurationFlattener.UnflattenConfiguration(currentFlat);
                            _logger.Debug("Loaded original config for change detection with {Count} records", currentRecordsForChange.Count);
                        }
                        else
                        {
                            _logger.Debug("No existing configuration found for change detection");
                        }
                    }
            
                    var flatConfig = ConfigurationFlattener.FlattenConfiguration(operation.Configuration);
                    _logger.Debug("Flattened configuration to {Count} key-value pairs", flatConfig.Count);
                    
                    // Determine keys to remove (only on full reset)
                    var keysToRemove = (operation.Type == OperationType.ResetConfiguration)
                        ? existingRecords.Keys.Except(flatConfig.Keys).ToList()
                        : new List<string>();
                    
                    if (keysToRemove.Any())
                    {
                        _logger.Debug("Removing {Count} obsolete configuration keys", keysToRemove.Count);
                        foreach (var keyToRemove in keysToRemove)
                        {
                            configurations.Delete(keyToRemove);
                        }
                    }
                    
                    // Determine keys to update
                    HashSet<string> keysToUpdate;
                    Dictionary<string, object> detectedChanges = null;

                    // If specific target keys were provided, respect them first
                    if (operation.TargetKeys != null && operation.TargetKeys.Any())
                    {
                        keysToUpdate = operation.TargetKeys.Where(k => flatConfig.ContainsKey(k)).ToHashSet();
                        _logger.Debug("Using TargetKeys override; will update {Count} targeted keys", keysToUpdate.Count);
                    }
                    else if ((operation.Type == OperationType.CreateConfiguration || operation.Type == OperationType.ResetConfiguration))
                    {
                        keysToUpdate = flatConfig.Keys.ToHashSet();
                    }
                    else if (operation.DetectChanges && originalConfig != null)
                    {
                        detectedChanges = _changeDetector.GetChanges(originalConfig, operation.Configuration);
                        keysToUpdate = detectedChanges.Keys.Where(k => flatConfig.ContainsKey(k)).ToHashSet();
                        _logger.Debug("Detected {Count} changed keys; will update only those.", keysToUpdate.Count);
                    }
                    else
                    {
                        keysToUpdate = flatConfig.Keys.ToHashSet();
                    }
                    
                    var updatedCount = 0;
                    var skippedAsNewer = 0;
                    var writtenKeys = new HashSet<string>();
                    foreach (var key in keysToUpdate)
                    {
                        var kv = flatConfig[key];
                        var newRecord = new ConfigurationRecord
                        {
                            Key = key,
                            Value = kv.value,
                            ValueType = kv.type,
                            LastModified = operation.Timestamp
                        };

                        if (existingRecords.TryGetValue(key, out var existing) && existing.LastModifiedTicks > newRecord.LastModifiedTicks)
                        {
                            skippedAsNewer++;
                            _logger.Debug("Skipping update for key '{Key}' because existing value is newer (existing: {ExistingTicks}, incoming: {IncomingTicks})", key, existing.LastModifiedTicks, newRecord.LastModifiedTicks);
                            continue;
                        }
                        
                        configurations.Upsert(newRecord);
                        updatedCount++;
                        writtenKeys.Add(key);

                        // Write history only for keys we actually wrote
                        var historyRecord = new ConfigurationHistoryRecord
                        {
                            Key = key,
                            Value = kv.value,
                            ValueType = kv.type,
                            ModifiedDate = operation.Timestamp,
                            ChangeDescription = operation.ChangeDescription,
                            SourceId = operation.SourceId
                        };
                        configHistory.Insert(historyRecord);
                    }
                    
                    _logger.Debug("Updated {Updated} configuration records in database (skipped {Skipped} newer records)", updatedCount, skippedAsNewer);
            
                    _configCache.Clear();
                    _logger.Debug("Cleared configuration cache");
                    
                    var eventsRaised = 0;
                    if (operation.DetectChanges && !operation.SuppressEvents && originalConfig != null)
                    {
                        detectedChanges ??= _changeDetector.GetChanges(originalConfig, operation.Configuration);
                        _logger.Debug("Detected {Count} changes for operation from source {SourceId}", 
                            detectedChanges.Count, operation.SourceId);
                        
                        foreach (var change in detectedChanges)
                        {
                            if (!writtenKeys.Contains(change.Key))
                            {
                                continue; // don’t raise events for values we didn’t actually write
                            }

                            _logger.Debug("Raising ConfigurationChanged event for '{PropertyPath}' = '{Value}' from source '{SourceId}'", 
                                change.Key, change.Value, operation.SourceId);
                            
                            ConfigurationChanged?.Invoke(
                                this,
                                new ConfigurationChangedEventArgs(change.Key, change.Value, operation.SourceId)
                            );
                            eventsRaised++;
                        }
                    }
                    else if (operation.Type == OperationType.CreateConfiguration || operation.Type == OperationType.ResetConfiguration)
                    {
                        // On create/reset, raise events for all written keys
                        foreach (var key in writtenKeys)
                        {
                            var kv = flatConfig[key];
                            ConfigurationChanged?.Invoke(this, new ConfigurationChangedEventArgs(key, kv.value, operation.SourceId));
                            eventsRaised++;
                        }
                    }
                    else if (operation.TargetKeys != null && operation.TargetKeys.Any() && !operation.SuppressEvents)
                    {
                        // When targeted keys are used (e.g., external single-property updates), raise events for written keys
                        foreach (var key in writtenKeys)
                        {
                            var kv = flatConfig[key];
                            ConfigurationChanged?.Invoke(this, new ConfigurationChangedEventArgs(key, kv.value, operation.SourceId));
                            eventsRaised++;
                        }
                    }
                    
                    _logger.Debug("Raised {Count} configuration change events", eventsRaised);
            
                    _logger.Info("Successfully processed configuration operation {Type}: wrote {Updated} keys (skipped {Skipped}) from source {SourceId}", 
                        operation.Type, updatedCount, skippedAsNewer, operation.SourceId ?? "unknown");
                    
                    _operationQueue.TryDequeue(out _);
                    _logger.Debug("Removed processed operation from tracking queue. Remaining: {Remaining}", _operationQueue.Count);
                }
                finally
                {
                    try { globalMutex.ReleaseMutex(); } catch { }
                }
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
                // Use global mutex to coordinate deletions across processes
                using var globalMutex = new Mutex(false, GlobalDbMutexName);
                var gotMutex = globalMutex.WaitOne(TimeSpan.FromSeconds(10));
                if (!gotMutex)
                {
                    throw new IOException("Timed out waiting for global DB mutex during history cleanup");
                }

                try
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
                }
                finally
                {
                    try { globalMutex.ReleaseMutex(); } catch { }
                }
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to cleanup configuration history");
        }
    }
}
