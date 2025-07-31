using CommonLib.Events;
using CommonLib.Models;

namespace CommonLib.Interfaces;

public interface IConfigurationService
{
    void CreateConfiguration();
    event EventHandler<ConfigurationChangedEventArgs> ConfigurationChanged;
    void UpdateConfigValue(Action<ConfigurationModel> propertyUpdater, string changedPropertyPath, object newValue);
    object ReturnConfigValue(Func<ConfigurationModel, object> propertySelector);
    void ResetToDefaultConfiguration();
    ConfigurationModel GetConfiguration();
    void SaveConfiguration(ConfigurationModel updatedConfig, bool detectChangesAndInvokeEvents = true);
    void UpdateConfigFromExternal(string propertyPath, object newValue);
    Task<string> ExportToFileAsync(string? filePath = null);
    Task FlushPendingChangesAsync();
    void FlushPendingChangesSync();
    int GetPendingOperationCount();
}