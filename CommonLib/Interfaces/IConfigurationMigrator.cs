using CommonLib.Models;

namespace CommonLib.Interfaces;

internal interface IConfigurationMigrator
{
    Task MigrateAsync(string databasePath, Func<ConfigurationOperation, Task> queueOperation);
}