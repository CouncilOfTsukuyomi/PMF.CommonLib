using CommonLib.Enums;

namespace CommonLib.Models;

internal class ConfigurationOperation
{
    public OperationType Type { get; set; }
    public ConfigurationModel Configuration { get; set; }
    public bool DetectChanges { get; set; } = true;
    public string ChangeDescription { get; set; }
    public DateTime Timestamp { get; set; }
    public string? SourceId { get; set; }
    public bool SuppressEvents { get; set; }
}