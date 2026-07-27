using System.Collections;
using System.Reflection;
using TelnetNegotiationCore.Models;

namespace MuClient.Core.Telnet;

/// <summary>
/// Projects a <see cref="MSSPConfig"/> into a flat string dictionary of the values the
/// server actually reported (non-null scalar and list properties), so consumers need not
/// depend on the library's model shape.
/// </summary>
internal static class MsspConfigReader
{
    private static readonly PropertyInfo[] Properties = typeof(MSSPConfig)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
        .ToArray();

    public static IReadOnlyDictionary<string, string> ToDictionary(MSSPConfig config)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (config is null)
        {
            return result;
        }

        foreach (var property in Properties)
        {
            object? value;
            try
            {
                value = property.GetValue(config);
            }
            catch
            {
                continue;
            }

            if (value is null)
            {
                continue;
            }

            var rendered = Render(value);
            if (!string.IsNullOrEmpty(rendered))
            {
                result[property.Name] = rendered;
            }
        }

        return result;
    }

    private static string Render(object value)
    {
        if (value is string s)
        {
            return s;
        }

        // Enumerable (e.g. a list of supported values) -> comma-joined.
        if (value is IEnumerable enumerable and not string)
        {
            var parts = new List<string>();
            foreach (var item in enumerable)
            {
                if (item is not null)
                {
                    parts.Add(item.ToString() ?? string.Empty);
                }
            }

            return string.Join(", ", parts);
        }

        return value.ToString() ?? string.Empty;
    }
}
