using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SharpMUTerm.Core.Configuration;

/// <summary>Loads and saves <see cref="AppConfiguration"/> as JSON.</summary>
public static class ConfigurationStore
{
    /// <summary>Shared serializer options: indented, string enums, and the colour converter.</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new TerminalColorJsonConverter());
        options.Converters.Add(new RgbJsonConverter());
        return options;
    }

    /// <summary>The default configuration path under the user's profile.</summary>
    public static string DefaultPath
    {
        get
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(baseDir))
            {
                baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            }

            return Path.Combine(baseDir, "MuGlyph", "config.json");
        }
    }

    public static AppConfiguration Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            return new AppConfiguration();
        }

        var json = File.ReadAllText(path);
        return Deserialize(json);
    }

    public static AppConfiguration Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        // Parse to a DOM first so older files can be upgraded to the current schema in place.
        if (JsonNode.Parse(json) is not JsonObject root)
        {
            return new AppConfiguration();
        }

        ConfigurationMigrator.Migrate(root);
        return root.Deserialize<AppConfiguration>(SerializerOptions) ?? new AppConfiguration();
    }

    public static string Serialize(AppConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return JsonSerializer.Serialize(configuration, SerializerOptions);
    }

    public static void Save(string path, AppConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(configuration);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Serialize(configuration));
    }
}
