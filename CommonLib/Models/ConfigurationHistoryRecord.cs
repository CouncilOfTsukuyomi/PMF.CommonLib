using LiteDB;

namespace CommonLib.Models;

public class ConfigurationHistoryRecord
{
    [BsonId]
    public ObjectId Id { get; set; }
    
    public string Key { get; set; } = string.Empty;
    
    public object Value { get; set; } = string.Empty;
    
    public string ValueType { get; set; } = string.Empty;
    
    [BsonField("modified")]
    public long ModifiedDateTicks { get; set; }
    
    [BsonIgnore]
    public DateTime ModifiedDate
    {
        get => new DateTime(ModifiedDateTicks);
        set => ModifiedDateTicks = value.Ticks;
    }
    
    public string ChangeDescription { get; set; } = string.Empty;
    public string? SourceId { get; set; }
}