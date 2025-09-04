namespace CommonLib.Interfaces;

public interface IMemoryMetricsService : IDisposable
{
    /// <summary>
    /// Starts periodic memory metrics logging.
    /// </summary>
    /// <param name="interval">Optional logging interval; defaults to 5 minutes.</param>
    void Start(TimeSpan? interval = null);

    /// <summary>
    /// Stops periodic memory metrics logging.
    /// </summary>
    void Stop();
}