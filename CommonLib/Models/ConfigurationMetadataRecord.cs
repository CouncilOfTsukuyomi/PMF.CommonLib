using LiteDB;

namespace CommonLib.Models;

public class ConfigurationMetadataRecord
{
    [BsonId]
    public string Id { get; set; } = "meta";

    public long LastMigrationTicks { get; set; }

    public string LastMigrationVersion { get; set; } = string.Empty;

    public string LastConfigHash { get; set; } = string.Empty;

    [BsonIgnore]
    public DateTime? LastMigrationUtc
    {
        get => LastMigrationTicks == 0 ? (DateTime?)null : new DateTime(LastMigrationTicks, DateTimeKind.Utc);
        set => LastMigrationTicks = value.HasValue ? value.Value.ToUniversalTime().Ticks : 0;
    }
}
