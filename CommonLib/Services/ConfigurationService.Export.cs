using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using CommonLib.Helper;
using CommonLib.Models;
using LiteDB;
using Newtonsoft.Json;

namespace CommonLib.Services;

public partial class ConfigurationService
{
    public async Task<string> ExportToFileAsync(string? filePath = null)
    {
        if (!_databaseInitialized)
        {
            _logger.Warn("Database not yet initialized, cannot export configuration");
            throw new InvalidOperationException("Database not yet initialized");
        }

        if (string.IsNullOrEmpty(filePath))
        {
            var configDir = Path.GetDirectoryName(_databasePath);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            filePath = Path.Combine(configDir, $"configuration_export_{timestamp}.json");
        }

        _logger.Info("Starting configuration export to file: {FilePath}", filePath);

        try
        {
            var json = await Task.Run(() =>
            {
                var connectionString = $"Filename={_databasePath};Connection=Shared;Timeout=00:00:10";
                using var database = new LiteDatabase(connectionString);

                var configurations = database.GetCollection<ConfigurationRecord>("configurations");
                var allRecords = configurations.FindAll().OrderBy(r => r.Key).ToList();

                if (!allRecords.Any())
                {
                    _logger.Info("No configuration records found in database");
                    return "{}";
                }
                
                var flatConfig = allRecords.ToDictionary(
                    r => r.Key, 
                    r => (r.Value, r.ValueType)
                );
                
                var reconstructedConfig = ConfigurationFlattener.UnflattenConfiguration(flatConfig);
                
                var currentConfig = GetConfiguration();
                var verificationResult = VerifyConfigurationMatch(currentConfig, reconstructedConfig);

                var export = new
                {
                    ExportInfo = new
                    {
                        ExportedAt = DateTime.UtcNow,
                        RecordCount = allRecords.Count,
                        FilePath = filePath,
                        VerificationPassed = verificationResult.IsMatch,
                        VerificationDetails = verificationResult.Details
                    },
                    Configuration = reconstructedConfig
                };

                return JsonConvert.SerializeObject(export, Formatting.Indented);
            });

            var directory = Path.GetDirectoryName(filePath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(filePath, json);

            _logger.Info("Configuration export completed successfully to {FilePath}, file size: {Size} bytes", 
                filePath, new FileInfo(filePath).Length);
            
            return filePath;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to export configuration to file: {FilePath}", filePath);
            throw;
        }
    }
    
    private (bool IsMatch, List<string> Details) VerifyConfigurationMatch(ConfigurationModel original, ConfigurationModel exported)
    {
        var details = new List<string>();
        var isMatch = true;

        try
        {
            var originalFlat = ConfigurationFlattener.FlattenConfiguration(original);
            var exportedFlat = ConfigurationFlattener.FlattenConfiguration(exported);
            
            var missingInExport = originalFlat.Keys.Except(exportedFlat.Keys).ToList();
            if (missingInExport.Any())
            {
                isMatch = false;
                details.Add($"Missing keys in export: {string.Join(", ", missingInExport)}");
            }
            
            var extraInExport = exportedFlat.Keys.Except(originalFlat.Keys).ToList();
            if (extraInExport.Any())
            {
                isMatch = false;
                details.Add($"Extra keys in export: {string.Join(", ", extraInExport)}");
            }
            
            var valueMismatches = new List<string>();
            foreach (var key in originalFlat.Keys.Intersect(exportedFlat.Keys))
            {
                var originalValue = originalFlat[key];
                var exportedValue = exportedFlat[key];

                if (!AreValuesEqual(originalValue.value, exportedValue.value) || 
                    originalValue.type != exportedValue.type)
                {
                    var originalValueStr = GetValueDisplayString(originalValue.value);
                    var exportedValueStr = GetValueDisplayString(exportedValue.value);
                    
                    valueMismatches.Add($"{key}: original='{originalValueStr}' ({originalValue.type}) vs exported='{exportedValueStr}' ({exportedValue.type})");
                }
            }

            if (valueMismatches.Any())
            {
                isMatch = false;
                details.Add($"Value mismatches: {string.Join("; ", valueMismatches)}");
            }

            if (isMatch)
            {
                details.Add($"All {originalFlat.Count} configuration keys match perfectly");
            }

            _logger.Debug("Configuration verification completed - Match: {IsMatch}, Keys checked: {Count}", 
                isMatch, originalFlat.Count);

            return (isMatch, details);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error during configuration verification");
            return (false, new List<string> { $"Verification error: {ex.Message}" });
        }
    }

    private string GetValueDisplayString(object value)
    {
        if (value == null) return "null";
        
        if (value is IEnumerable enumerable && !(value is string))
        {
            var items = enumerable.Cast<object>().Select(x => x?.ToString() ?? "null");
            return $"[{string.Join(", ", items)}]";
        }
        
        return value.ToString() ?? "null";
    }

    private bool AreValuesEqual(object value1, object value2)
    {
        if (value1 == null && value2 == null) return true;
        if (value1 == null || value2 == null) return false;
        
        if (value1 is IEnumerable enum1 && value2 is IEnumerable enum2 && 
            !(value1 is string) && !(value2 is string))
        {
            return AreCollectionsEqual(enum1, enum2);
        }
    
        if (IsNumericType(value1) && IsNumericType(value2))
        {
            try
            {
                var decimal1 = Convert.ToDecimal(value1);
                var decimal2 = Convert.ToDecimal(value2);
                return decimal1 == decimal2;
            }
            catch
            {
                return false;
            }
        }
    
        return value1.Equals(value2);
    }

    private bool AreCollectionsEqual(IEnumerable collection1, IEnumerable collection2)
    {
        var list1 = collection1.Cast<object>().ToList();
        var list2 = collection2.Cast<object>().ToList();
    
        if (list1.Count != list2.Count)
            return false;
    
        for (int i = 0; i < list1.Count; i++)
        {
            if (!AreValuesEqual(list1[i], list2[i]))
                return false;
        }
    
        return true;
    }

    private bool IsNumericType(object value)
    {
        return value is sbyte || value is byte || value is short || value is ushort ||
               value is int || value is uint || value is long || value is ulong ||
               value is float || value is double || value is decimal;
    }
}
