using System.Collections;
using System.Reflection;
using CommonLib.Models;

namespace CommonLib.Helper;

internal static class ConfigurationFlattener
{
    public static Dictionary<string, (object value, string type)> FlattenConfiguration(ConfigurationModel config)
    {
        var result = new Dictionary<string, (object value, string type)>();
        FlattenObject(config, "", result);
        return result;
    }

    public static ConfigurationModel UnflattenConfiguration(Dictionary<string, (object value, string type)> flatConfig)
    {
        var config = new ConfigurationModel();
        
        foreach (var kvp in flatConfig)
        {
            SetNestedProperty(config, kvp.Key, kvp.Value.value);
        }
        
        return config;
    }

    private static void FlattenObject(object obj, string prefix, Dictionary<string, (object value, string type)> result)
    {
        if (obj == null) return;

        var type = obj.GetType();
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var property in properties)
        {
            var value = property.GetValue(obj);
            var key = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";

            if (value == null)
            {
                result[key] = (null, property.PropertyType.Name);
            }
            else if (IsSimpleType(property.PropertyType))
            {
                result[key] = (value, property.PropertyType.Name);
            }
            else if (property.PropertyType.IsGenericType && 
                     property.PropertyType.GetGenericTypeDefinition() == typeof(List<>))
            {
                // Handle List<T>
                result[key] = (value, property.PropertyType.Name);
            }
            else if (property.PropertyType.IsArray)
            {
                // Handle arrays
                result[key] = (value, property.PropertyType.Name);
            }
            else
            {
                // Handle nested objects (UI, BackgroundWorker, etc.)
                FlattenObject(value, key, result);
            }
        }
    }

    private static void SetNestedProperty(object obj, string propertyPath, object value)
    {
        var properties = propertyPath.Split('.');
        object currentObject = obj;

        for (int i = 0; i < properties.Length - 1; i++)
        {
            var propertyInfo = currentObject.GetType().GetProperty(properties[i]);
            if (propertyInfo == null) return;

            var nextObject = propertyInfo.GetValue(currentObject);
            if (nextObject == null)
            {
                // Create instance of the nested object
                nextObject = Activator.CreateInstance(propertyInfo.PropertyType);
                propertyInfo.SetValue(currentObject, nextObject);
            }
            currentObject = nextObject;
        }

        var finalProperty = currentObject.GetType().GetProperty(properties.Last());
        if (finalProperty != null && value != null)
        {
            // Handle type conversion if needed
            if (value.GetType() != finalProperty.PropertyType)
            {
                if (finalProperty.PropertyType.IsGenericType && 
                    finalProperty.PropertyType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    // Handle List conversion
                    if (value is IEnumerable enumerable && !(value is string))
                    {
                        var listType = typeof(List<>).MakeGenericType(finalProperty.PropertyType.GetGenericArguments()[0]);
                        var list = Activator.CreateInstance(listType) as IList;
                        foreach (var item in enumerable)
                        {
                            list?.Add(item);
                        }
                        value = list;
                    }
                }
                else
                {
                    value = Convert.ChangeType(value, finalProperty.PropertyType);
                }
            }
            
            finalProperty.SetValue(currentObject, value);
        }
    }

    private static bool IsSimpleType(Type type)
    {
        return type.IsPrimitive || 
               type == typeof(string) || 
               type == typeof(DateTime) || 
               type == typeof(decimal) || 
               type == typeof(Guid) ||
               type.IsEnum ||
               (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>) && 
                IsSimpleType(type.GetGenericArguments()[0]));
    }
}