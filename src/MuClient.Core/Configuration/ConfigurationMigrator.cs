using System.Text.Json.Nodes;

namespace MuClient.Core.Configuration;

/// <summary>
/// Upgrades an on-disk configuration DOM to <see cref="AppConfiguration.CurrentVersion"/>. Runs
/// before deserialization so old files keep working after the schema moved automation and login
/// details off the world and onto shared trigger sets and per-world characters.
/// </summary>
public static class ConfigurationMigrator
{
    /// <summary>
    /// Mutates <paramref name="root"/> in place, applying each version step needed to bring it up
    /// to the current schema. Unknown or already-current documents are left untouched.
    /// </summary>
    public static void Migrate(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var version = root["version"]?.GetValue<int>() ?? 1;
        if (version < 2)
        {
            MigrateV1ToV2(root);
        }

        root["version"] = AppConfiguration.CurrentVersion;
    }

    /// <summary>
    /// v1 stored triggers/aliases/macros/scriptFiles and logging on each world. v2 lifts each
    /// world's automation into a shared trigger set and gives the world a default character that
    /// opts into that set and carries the old logging.
    /// </summary>
    private static void MigrateV1ToV2(JsonObject root)
    {
        if (root["worlds"] is not JsonArray worlds)
        {
            return;
        }

        var triggerSets = root["triggerSets"] as JsonArray;
        if (triggerSets is null)
        {
            triggerSets = new JsonArray();
            root["triggerSets"] = triggerSets;
        }

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in triggerSets.OfType<JsonObject>())
        {
            if (set["name"]?.GetValue<string>() is { } existing)
            {
                usedNames.Add(existing);
            }
        }

        foreach (var node in worlds)
        {
            if (node is JsonObject world)
            {
                MigrateWorld(world, triggerSets, usedNames);
            }
        }
    }

    private static void MigrateWorld(JsonObject world, JsonArray triggerSets, HashSet<string> usedNames)
    {
        var worldName = world["name"]?.GetValue<string>() ?? "Imported World";

        var triggers = Detach(world, "triggers");
        var aliases = Detach(world, "aliases");
        var macros = Detach(world, "macros");
        var scriptFiles = Detach(world, "scriptFiles");
        var logging = Detach(world, "logging");

        var hasAutomation = HasItems(triggers) || HasItems(aliases) || HasItems(macros) || HasItems(scriptFiles);

        // Preserve an existing v2 characters array if a partially-migrated file supplies one.
        if (world["characters"] is JsonArray existingCharacters && existingCharacters.Count > 0 && !hasAutomation)
        {
            return;
        }

        string? setName = null;
        if (hasAutomation)
        {
            setName = UniqueName(worldName, usedNames);
            triggerSets.Add(new JsonObject
            {
                ["name"] = setName,
                ["description"] = $"Migrated from world '{worldName}'.",
                ["triggers"] = triggers ?? new JsonArray(),
                ["aliases"] = aliases ?? new JsonArray(),
                ["macros"] = macros ?? new JsonArray(),
                ["scriptFiles"] = scriptFiles ?? new JsonArray(),
            });
        }

        if (world["characters"] is not JsonArray)
        {
            var character = new JsonObject { ["name"] = worldName };
            if (setName is not null)
            {
                character["triggerSets"] = new JsonArray(setName);
            }

            if (logging is not null)
            {
                character["logging"] = logging;
            }

            world["characters"] = new JsonArray(character);
        }
    }

    /// <summary>Removes <paramref name="name"/> from <paramref name="obj"/> and returns the detached node.</summary>
    private static JsonNode? Detach(JsonObject obj, string name)
    {
        if (!obj.TryGetPropertyValue(name, out var node) || node is null)
        {
            obj.Remove(name);
            return null;
        }

        obj.Remove(name);
        return node;
    }

    private static bool HasItems(JsonNode? node) => node is JsonArray array && array.Count > 0;

    private static string UniqueName(string worldName, HashSet<string> used)
    {
        var candidate = worldName;
        var suffix = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{worldName} ({suffix++})";
        }

        return candidate;
    }
}
