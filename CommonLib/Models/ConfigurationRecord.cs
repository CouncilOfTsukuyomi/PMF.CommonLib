using LiteDB;

namespace CommonLib.Models;
public class ConfigurationRecord
{
    [BsonId]
    public string Key { get; set; } = string.Empty;
    
    public object Value { get; set; } = string.Empty;
    
    public string ValueType { get; set; } = string.Empty;
    
    [BsonField("modified")]
    public long LastModifiedTicks { get; set; }
    
    [BsonIgnore]
    public DateTime LastModified
    {
        get => new DateTime(LastModifiedTicks);
        set => LastModifiedTicks = value.Ticks;
    }
}