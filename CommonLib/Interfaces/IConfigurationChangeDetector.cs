using CommonLib.Models;

namespace CommonLib.Interfaces;

internal interface IConfigurationChangeDetector
{
    Dictionary<string, object> GetChanges(ConfigurationModel original, ConfigurationModel updated);
}