using Newtonsoft.Json;

namespace CommonLib.Extensions;

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