﻿using System.Reflection;
using KellermanSoftware.CompareNetObjects;
using Newtonsoft.Json;
using PenumbraModForwarder.Common.Consts;
using PenumbraModForwarder.Common.Events;
using PenumbraModForwarder.Common.Interfaces;
using PenumbraModForwarder.Common.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace PenumbraModForwarder.Common.Services;

public class ConfigurationService : IConfigurationService
{
    private readonly IFileStorage _fileStorage;
    private readonly ILogger _logger;
    private ConfigurationModel _config;
    public event EventHandler<ConfigurationChangedEventArgs> ConfigurationChanged;

    public ConfigurationService(IFileStorage fileStorage)
    {
        _fileStorage = fileStorage;
        _logger = Log.ForContext<ConfigurationService>();
        LoadConfiguration();
    }

    private void LoadConfiguration()
    {
        if (_fileStorage.Exists(ConfigurationConsts.ConfigurationFilePath))
        {
            var configContent = _fileStorage.Read(ConfigurationConsts.ConfigurationFilePath);
            _config = JsonConvert.DeserializeObject<ConfigurationModel>(configContent) ?? new ConfigurationModel();
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
            _logger.Information("Configuration file created with default values.");
        }
        else
        {
            _logger.Information("Configuration file already exists.");
        }
    }

    public ConfigurationModel GetConfiguration()
    {
        return _config;
    }

    public void SaveConfiguration(ConfigurationModel updatedConfig, bool detectChangesAndInvokeEvents = true)
    {
        if (updatedConfig == null) throw new ArgumentNullException(nameof(updatedConfig));
        if (detectChangesAndInvokeEvents)
        {
            var originalConfig = _config.DeepClone();
            _config = updatedConfig;
            var changes = GetChanges(originalConfig, _config);
            if (changes.Any())
            {
                foreach (var change in changes)
                {
                    ConfigurationChanged?.Invoke(this, new ConfigurationChangedEventArgs(change.Key, change.Value));
                }
            }
        }
        else
        {
            _config = updatedConfig;
        }
        var updatedConfigContent = JsonConvert.SerializeObject(_config, Formatting.Indented);
        _fileStorage.Write(ConfigurationConsts.ConfigurationFilePath, updatedConfigContent);
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
        {
            throw new ArgumentNullException(nameof(propertySelector));
        }
        return propertySelector(_config);
    }

    public void UpdateConfigValue(Action<ConfigurationModel> propertyUpdater, string changedPropertyPath, object newValue)
    {
        if (propertyUpdater == null)
        {
            throw new ArgumentNullException(nameof(propertyUpdater), "Property updater cannot be null.");
        }
        propertyUpdater(_config);
        _logger.Debug("Raising ConfigurationChanged event for {ChangedPropertyPath} with new value: {NewValue}", changedPropertyPath, newValue);
        ConfigurationChanged?.Invoke(this, new ConfigurationChangedEventArgs(changedPropertyPath, newValue));
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
                throw new Exception($"Property '{propertyName}' not found on type '{currentObject.GetType().Name}'");
            if (i == properties.Length - 1)
            {
                // Convert the new value to the correct type
                var convertedValue = Convert.ChangeType(newValue, propertyInfo.PropertyType);
                propertyInfo.SetValue(currentObject, convertedValue);
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
                _logger.Debug("Detected change in property '{PropertyName}': Original Value = '{OriginalValue}', New Value = '{NewValue}'", propertyName, difference.Object1, difference.Object2);
            }
        }
        else
        {
            _logger.Debug("No differences detected between original and updated configurations.");
        }
        return changes;
    }
}

// Extension method for deep cloning
public static class CloneExtensions
{
    public static T DeepClone<T>(this T obj)
    {
        if (obj == null) return default(T);
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