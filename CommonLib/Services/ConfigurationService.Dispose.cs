namespace CommonLib.Services;

public partial class ConfigurationService
{
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
