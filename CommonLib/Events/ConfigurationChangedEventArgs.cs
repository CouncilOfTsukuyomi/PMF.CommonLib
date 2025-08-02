namespace CommonLib.Events;

public class ConfigurationChangedEventArgs : EventArgs
{
    public string PropertyName { get; set; }
    public object NewValue { get; set; }
    public string? SourceId { get; set; }
    public DateTime Timestamp { get; set; }

    public ConfigurationChangedEventArgs(string propertyName, object newValue, string? sourceId = null)
    {
        PropertyName = propertyName;
        NewValue = newValue;
        SourceId = sourceId;
        Timestamp = DateTime.UtcNow;
    }
}