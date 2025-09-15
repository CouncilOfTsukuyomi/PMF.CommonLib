
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

public partial class ConfigurationService : IConfigurationService, IDisposable
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    private readonly string _databasePath;
    private readonly IMapper _mapper;
    private readonly IFileStorage _fileStorage;
    private readonly IConfigurationMigrator _migrator;
    private readonly IConfigurationChangeDetector _changeDetector;
    
    private static readonly SemaphoreSlim _globalDbSemaphore = new(1, 1);
    private static readonly SemaphoreSlim _migrationSemaphore = new(1, 1);
    private const string GlobalDbMutexName = "Global\\Atomos.ConfigurationDb";
    
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






    

}