﻿using System.Reflection;
using KellermanSoftware.CompareNetObjects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using PenumbraModForwarder.Common.Consts;
using PenumbraModForwarder.Common.Events;
using PenumbraModForwarder.Common.Interfaces;
using PenumbraModForwarder.Common.Models;

namespace PenumbraModForwarder.Common.Services;

public class ConfigurationService : IConfigurationService
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    private readonly IFileStorage _fileStorage;
    private ConfigurationModel _config;
    private readonly object _configWriteLock = new object();

    public event EventHandler<ConfigurationChangedEventArgs>? ConfigurationChanged;

    public ConfigurationService(IFileStorage fileStorage)
    {
        _fileStorage = fileStorage;
        LoadConfiguration();
    }

    private void LoadConfiguration()
    {
        if (_fileStorage.Exists(ConfigurationConsts.ConfigurationFilePath))
        {
            try
            {
                using var stream = _fileStorage.OpenRead(ConfigurationConsts.ConfigurationFilePath);
                using var reader = new StreamReader(stream);
                var configContent = reader.ReadToEnd();
                _config = JsonConvert.DeserializeObject<ConfigurationModel>(configContent)
                          ?? new ConfigurationModel();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to load configuration file. Falling back to default configuration.");
                _config = new ConfigurationModel();
            }
        }
        else
        {
            _config = new ConfigurationModel();
        }
    }

    public void CreateConfiguration()
    {
        var configDirectory = Path.GetDirectoryName(ConfigurationConsts.ConfigurationFilePath);

        if (!_fileStorage.Exists(configDirectory))
        {
            _fileStorage.CreateDirectory(configDirectory);
        }

        if (!_fileStorage.Exists(ConfigurationConsts.ConfigurationFilePath))
        {
            _config = new ConfigurationModel();
            SaveConfiguration(_config);
            _logger.Info("Configuration file created with default values.");
        }
        else
        {
            _logger.Info("Configuration file already exists.");
        }
    }

    public ConfigurationModel GetConfiguration()
    {
        return _config;
    }

    public void SaveConfiguration(ConfigurationModel updatedConfig, bool detectChangesAndInvokeEvents = true)
    {
        if (updatedConfig == null)
            throw new ArgumentNullException(nameof(updatedConfig));

        if (detectChangesAndInvokeEvents)
        {
            var originalConfig = _config.DeepClone();
            _config = updatedConfig;

            var changes = GetChanges(originalConfig, _config);
            if (changes.Any())
            {
                foreach (var change in changes)
                {
                    ConfigurationChanged?.Invoke(
                        this,
                        new ConfigurationChangedEventArgs(change.Key, change.Value)
                    );
                }
            }
        }
        else
        {
            _config = updatedConfig;
        }

        var updatedConfigContent = JsonConvert.SerializeObject(_config, Formatting.Indented);

        lock (_configWriteLock)
        {
            try
            {
                using var stream = _fileStorage.OpenWrite(ConfigurationConsts.ConfigurationFilePath);
                using var writer = new StreamWriter(stream);
                writer.Write(updatedConfigContent);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to save configuration to {Path}", ConfigurationConsts.ConfigurationFilePath);
                throw;
            }
        }
    }

    public void ResetToDefaultConfiguration()
    {
        _config = new ConfigurationModel();
        SaveConfiguration(_config);
        ConfigurationChanged?.Invoke(this, new ConfigurationChangedEventArgs("All", _config));
    }

    public object ReturnConfigValue(Func<ConfigurationModel, object> propertySelector)
    {
        if (propertySelector == null)
            throw new ArgumentNullException(nameof(propertySelector));

        return propertySelector(_config);
    }

    public void UpdateConfigValue(Action<ConfigurationModel> propertyUpdater, string changedPropertyPath, object newValue)
    {
        if (propertyUpdater == null)
            throw new ArgumentNullException(nameof(propertyUpdater), "Property updater cannot be null.");

        propertyUpdater(_config);

        _logger.Debug(
            "Raising ConfigurationChanged event for {ChangedPropertyPath} with new value: {NewValue}",
            changedPropertyPath,
            newValue
        );

        ConfigurationChanged?.Invoke(this, new ConfigurationChangedEventArgs(changedPropertyPath, newValue));

        // Save without re-checking changes to avoid re-raising.  
        SaveConfiguration(_config, detectChangesAndInvokeEvents: false);
    }

    public void UpdateConfigFromExternal(string propertyPath, object newValue)
    {
        SetPropertyValue(_config, propertyPath, newValue);
        ConfigurationChanged?.Invoke(this, new ConfigurationChangedEventArgs(propertyPath, newValue));
        SaveConfiguration(_config, detectChangesAndInvokeEvents: false);
    }

    private void SetPropertyValue(object obj, string propertyPath, object newValue)
    {
        var properties = propertyPath.Split('.');
        object currentObject = obj;
        PropertyInfo propertyInfo = null;

        for (int i = 0; i < properties.Length; i++)
        {
            var propertyName = properties[i];
            propertyInfo = currentObject.GetType().GetProperty(propertyName);

            if (propertyInfo == null)
            {
                throw new Exception(
                    $"Property '{propertyName}' not found on type '{currentObject.GetType().Name}'"
                );
            }

            if (i == properties.Length - 1)
            {
                if (newValue is JArray jArrayValue)
                {
                    // Convert JArray to either List<string>, string[], or any other collection
                    if (propertyInfo.PropertyType == typeof(List<string>))
                    {
                        var typedList = jArrayValue.ToObject<List<string>>();
                        propertyInfo.SetValue(currentObject, typedList);
                    }
                    else if (propertyInfo.PropertyType.IsArray &&
                             propertyInfo.PropertyType.GetElementType() == typeof(string))
                    {
                        var stringArray = jArrayValue.ToObject<string[]>();
                        propertyInfo.SetValue(currentObject, stringArray);
                    }
                    else
                    {
                        var convertedCollection = jArrayValue.ToObject(propertyInfo.PropertyType);
                        propertyInfo.SetValue(currentObject, convertedCollection);
                    }
                }
                else
                {
                    var convertedValue = Convert.ChangeType(newValue, propertyInfo.PropertyType);
                    propertyInfo.SetValue(currentObject, convertedValue);
                }
            }
            else
            {
                currentObject = propertyInfo.GetValue(currentObject);
            }
        }
    }

    private Dictionary<string, object> GetChanges(ConfigurationModel original, ConfigurationModel updated)
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

/// <summary>
/// Extension method for deep cloning
/// </summary>
public static class CloneExtensions
{
    public static T DeepClone<T>(this T obj)
    {
        if (obj == null) return default;

        var settings = new JsonSerializerSettings
        {
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            PreserveReferencesHandling = PreserveReferencesHandling.Objects,
            TypeNameHandling = TypeNameHandling.Auto,
            Formatting = Formatting.Indented
        };

        var serialized = JsonConvert.SerializeObject(obj, settings);
        return JsonConvert.DeserializeObject<T>(serialized, settings);
    }
}