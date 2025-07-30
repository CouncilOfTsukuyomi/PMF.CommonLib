using System.Collections;
using System.Reflection;
using CommonLib.Models;
using Newtonsoft.Json;
using NLog;

namespace CommonLib.Helper;

internal static class ConfigurationFlattener
{
    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
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
            try
            {
                SetNestedProperty(config, kvp.Key, kvp.Value.value, kvp.Value.type);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to set property {kvp.Key}: {ex.Message}");
            }
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
                result[key] = (null, property.PropertyType.AssemblyQualifiedName ?? property.PropertyType.Name);
            }
            else if (IsSimpleType(property.PropertyType))
            {
                result[key] = (value, property.PropertyType.AssemblyQualifiedName ?? property.PropertyType.Name);
            }
            else if (property.PropertyType.IsGenericType && 
                     property.PropertyType.GetGenericTypeDefinition() == typeof(List<>))
            {
                var jsonValue = JsonConvert.SerializeObject(value);
                result[key] = (jsonValue, property.PropertyType.AssemblyQualifiedName ?? property.PropertyType.Name);
            }
            else if (property.PropertyType.IsArray)
            {
                var jsonValue = JsonConvert.SerializeObject(value);
                result[key] = (jsonValue, property.PropertyType.AssemblyQualifiedName ?? property.PropertyType.Name);
            }
            else
            {
                FlattenObject(value, key, result);
            }
        }
    }

    private static void SetNestedProperty(object obj, string propertyPath, object value, string typeInfo = null)
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
                nextObject = Activator.CreateInstance(propertyInfo.PropertyType);
                propertyInfo.SetValue(currentObject, nextObject);
            }
            currentObject = nextObject;
        }
        
        var finalProperty = currentObject.GetType().GetProperty(properties.Last());
        if (finalProperty == null || value == null) return;

        try
        {
            var convertedValue = ConvertValueToTargetType(value, finalProperty.PropertyType, typeInfo);
            finalProperty.SetValue(currentObject, convertedValue);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to set property '{propertyPath}' with value '{value}' (type: {value?.GetType()?.Name}) to target type '{finalProperty.PropertyType.Name}': {ex.Message}",
                ex);
        }
    }

    private static object ConvertValueToTargetType(object value, Type targetType, string typeInfo = null)
    {
        if (value == null) return null;
        
        var valueType = value.GetType();
        
        if (valueType == targetType || targetType.IsAssignableFrom(valueType))
        {
            return value;
        }
        
        if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            var underlyingType = Nullable.GetUnderlyingType(targetType);
            if (underlyingType != null)
            {
                return ConvertValueToTargetType(value, underlyingType, typeInfo);
            }
        }
        
        if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(List<>))
        {
            return ConvertToList(value, targetType, typeInfo);
        }
        
        if (targetType.IsArray)
        {
            return ConvertToArray(value, targetType, typeInfo);
        }
        
        if (targetType.IsEnum)
        {
            if (value is string stringValue)
            {
                return Enum.Parse(targetType, stringValue);
            }
            return Enum.ToObject(targetType, value);
        }
        
        if (targetType == typeof(Guid))
        {
            if (value is string guidString)
            {
                return Guid.Parse(guidString);
            }
        }
        
        if (targetType == typeof(string))
        {
            return value.ToString();
        }
        
        try
        {
            return Convert.ChangeType(value, targetType);
        }
        catch (Exception ex)
        {
            throw new InvalidCastException(
                $"Cannot convert value '{value}' of type '{valueType.Name}' to target type '{targetType.Name}'", ex);
        }
    }

    private static object ConvertToList(object value, Type listType, string typeInfo)
    {
        var elementType = listType.GetGenericArguments()[0];
        var listInstance = Activator.CreateInstance(listType) as IList;
        
        if (value is string jsonString && (jsonString.StartsWith("[") || jsonString.StartsWith("{")))
        {
            try
            {
                var deserializedList = JsonConvert.DeserializeObject(jsonString, listType);
                if (deserializedList != null)
                {
                    return deserializedList;
                }
            }
            catch
            {
            }
        }

        if (value is IEnumerable enumerable && !(value is string))
        {
            foreach (var item in enumerable)
            {
                var convertedItem = ConvertValueToTargetType(item, elementType, null);
                listInstance?.Add(convertedItem);
            }
            return listInstance;
        }

        if (value != null)
        {
            var convertedItem = ConvertValueToTargetType(value, elementType, null);
            listInstance?.Add(convertedItem);
            return listInstance;
        }

        return listInstance;
    }

    private static object ConvertToArray(object value, Type arrayType, string typeInfo)
    {
        var elementType = arrayType.GetElementType();

        if (value is string jsonString && (jsonString.StartsWith("[") || jsonString.StartsWith("{")))
        {
            try
            {
                var deserializedArray = JsonConvert.DeserializeObject(jsonString, arrayType);
                if (deserializedArray != null)
                {
                    return deserializedArray;
                }
            }
            catch
            {
                // ignored
            }
        }

        if (value is IEnumerable enumerable && !(value is string))
        {
            var tempList = new List<object>();
            foreach (var item in enumerable)
            {
                var convertedItem = ConvertValueToTargetType(item, elementType, null);
                tempList.Add(convertedItem);
            }
            
            var array = Array.CreateInstance(elementType, tempList.Count);
            for (int i = 0; i < tempList.Count; i++)
            {
                array.SetValue(tempList[i], i);
            }
            return array;
        }

        if (value != null)
        {
            var array = Array.CreateInstance(elementType, 1);
            var convertedItem = ConvertValueToTargetType(value, elementType, null);
            array.SetValue(convertedItem, 0);
            return array;
        }

        return Array.CreateInstance(elementType, 0);
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