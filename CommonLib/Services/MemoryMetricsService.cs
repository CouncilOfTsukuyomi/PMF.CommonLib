using System.Diagnostics;
using System.Runtime;
using CommonLib.Interfaces;
using NLog;

namespace CommonLib.Services;

public sealed class MemoryMetricsService : IMemoryMetricsService
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private Timer? _timer;
    private bool _started;
    private readonly object _lock = new();

    public void Start(TimeSpan? interval = null)
    {
        lock (_lock)
        {
            if (_started)
                return;

            var dueTime = TimeSpan.FromSeconds(5);
            var period = interval ?? TimeSpan.FromMinutes(5);
            
            TryLogSnapshot("startup");

            _timer = new Timer(_ => TryLogSnapshot("periodic"), null, dueTime, period);
            _started = true;
            _logger.Debug("MemoryMetricsService started with interval {Interval}", period);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_started)
                return;

            try
            {
                _timer?.Dispose();
            }
            catch {}
            finally
            {
                _timer = null;
                _started = false;
                _logger.Debug("MemoryMetricsService stopped");
            }
        }
    }

    private void TryLogSnapshot(string reason)
    {
        try
        {
            var proc = Process.GetCurrentProcess();
            proc.Refresh();

            long workingSet = proc.WorkingSet64;
            long privateBytes = proc.PrivateMemorySize64;
            int threads = proc.Threads.Count;

            var gcInfo = GC.GetGCMemoryInfo();
            long heapSize = gcInfo.HeapSizeBytes;
            long totalCommitted = gcInfo.TotalCommittedBytes;
            long totalAvailable = gcInfo.TotalAvailableMemoryBytes;
            var latency = GCSettings.LatencyMode;

            _logger.Info(
                "[MEM] Reason={Reason} WS={WS_MB}MB Private={Private_MB}MB Heap={Heap_MB}MB Committed={Committed_MB}MB Avail={Avail_MB}MB Gen0={G0} Gen1={G1} Gen2={G2} LOH={LOH_MB}MB Threads={Threads} Latency={Latency}",
                reason,
                ToMB(workingSet),
                ToMB(privateBytes),
                ToMB(heapSize),
                ToMB(totalCommitted),
                ToMB(totalAvailable),
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2),
                ToMB(gcInfo.GenerationInfo.Length > 2 ? gcInfo.GenerationInfo[2].SizeBeforeBytes : 0),
                threads,
                latency);
        }
        catch (Exception ex)
        {
            _logger.Warn(ex, "MemoryMetricsService failed to log a snapshot");
        }
    }

    private static long ToMB(long bytes) => (long)(bytes / (1024.0 * 1024.0));

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}