using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CommonLib.Models;
using Newtonsoft.Json;

namespace CommonLib.Helper;

internal static class ConfigurationHashUtil
{
    public static string ComputeHash(ConfigurationModel model)
    {
        if (model == null) return string.Empty;
        var flat = ConfigurationFlattener.FlattenConfiguration(model);

        // Build a deterministic JSON string: ordered by key, include type and normalized value
        var items = new List<object>();
        foreach (var key in flat.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var (value, type) = flat[key];
            items.Add(new { k = key, t = type, v = NormalizeValue(value) });
        }

        var json = JsonConvert.SerializeObject(items, new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Include,
            Formatting = Formatting.None
        });

        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(json);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }

    private static object NormalizeValue(object value)
    {
        if (value == null) return null;

        switch (value)
        {
            case bool b:
                return b ? "true" : "false";
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            case float f:
                return f.ToString("R", CultureInfo.InvariantCulture);
            case double d:
                return d.ToString("R", CultureInfo.InvariantCulture);
            case decimal m:
                return m.ToString(CultureInfo.InvariantCulture);
            case DateTime dt:
                return dt.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
            default:
                return value.ToString();
        }
    }
}
