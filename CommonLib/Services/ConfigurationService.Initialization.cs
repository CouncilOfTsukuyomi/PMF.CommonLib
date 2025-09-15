using CommonLib.Consts;
using CommonLib.Models;
using LiteDB;

namespace CommonLib.Services;

public partial class ConfigurationService
{
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

                    // Cross-process coordination: ensure only one process migrates at a time
                    using var globalMutex = new Mutex(false, GlobalDbMutexName);
                    var gotMutex = globalMutex.WaitOne(TimeSpan.FromSeconds(30));
                    if (!gotMutex)
                    {
                        throw new IOException("Timed out waiting for global DB mutex during migration");
                    }

                    try
                    {
                        await _migrator.MigrateAsync(_databasePath, QueueConfigurationOperationAsync);
                    }
                    finally
                    {
                        try { globalMutex.ReleaseMutex(); } catch { }
                    }
                    
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
}
