using CommonLib.Interfaces;
using CommonLib.Models;
using KellermanSoftware.CompareNetObjects;
using NLog;

namespace CommonLib.Services;

internal class ConfigurationChangeDetector : IConfigurationChangeDetector
{
    private readonly Logger _logger;

    public ConfigurationChangeDetector(Logger logger)
    {
        _logger = logger;
    }

    public Dictionary<string, object> GetChanges(ConfigurationModel original, ConfigurationModel updated)
    {
        var changes = new Dictionary<string, object>();
        var compareLogic = new CompareLogic
        {
            Config =
            {
                MaxDifferences = int.MaxValue,
                IgnoreObjectTypes = false,
                CompareFields = true,
                CompareProperties = true,
                ComparePrivateFields = false,
                ComparePrivateProperties = false,
                IgnoreCollectionOrder = false,
                Caching = false
            }
        };

        var comparisonResult = compareLogic.Compare(original, updated);

        if (!comparisonResult.AreEqual)
        {
            foreach (var difference in comparisonResult.Differences)
            {
                var propertyName = difference.PropertyName.TrimStart('.');
                var newValue = difference.Object2;
                changes[propertyName] = newValue;

                _logger.Debug(
                    "Detected change in property '{PropertyName}': Original Value = '{OriginalValue}', New Value = '{NewValue}'",
                    propertyName,
                    difference.Object1,
                    difference.Object2
                );
            }
        }
        else
        {
            _logger.Debug("No differences detected between original and updated configurations.");
        }

        return changes;
    }
}