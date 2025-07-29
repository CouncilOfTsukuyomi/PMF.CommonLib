using System.Reflection;
using Newtonsoft.Json.Linq;

namespace CommonLib.Helper;

internal static class PropertyUpdater
{
    public static void SetPropertyValue(object obj, string propertyPath, object newValue)
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
                    // Handle JArray -> List<string> or string[]
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
}