using System.Text.Json.Nodes;

namespace SharpMUTerm.Core.Configuration;

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

        if (version < 3)
        {
            MigrateV2ToV3(root);
        }

        if (version < 4)
        {
            MigrateV3ToV4(root);
        }

        if (version < 5)
        {
            MigrateV4ToV5(root);
        }

        root["version"] = AppConfiguration.CurrentVersion;
    }

    /// <summary>
    /// v4's spawn window ids named only their target (<c>spawn:Public</c>), so a workspace held one
    /// capture pane per target however many characters were capturing into it. v5 puts the owning
    /// session in the id (<see cref="Workspaces.Workspace.SpawnWindowId(string?,string)"/>), so two
    /// connected characters running the same rule get a pane each. Every saved id is rewritten here —
    /// in <c>lastSession.windows</c> and in every pane's <c>tabs</c> array that referenced it.
    /// <para>
    /// <b>It is a migration and not an adoption, and the reason is that a resumed workspace must not
    /// come back holding a pane nothing writes to.</b> Left alone, a saved <c>spawn:Public</c> would
    /// match no id the running client can now produce: the pane would sit there for ever, empty, while
    /// its channel filled a second pane beside it. Dropping it would have been the other way to avoid
    /// that and it throws away a pane the user had, plus — through the window id the restore log is
    /// keyed by — the scrollback in it. Rewriting keeps both: the pane stays where it was, and its log
    /// file is carried across at startup by <c>SharpMUTermApp.RestorePreviousSession</c>.
    /// </para>
    /// <para>
    /// <b>What supplies the owner is the state itself.</b> <c>WorkspaceWindowState.SessionKey</c> has
    /// been persisted all along, so an old file already records which character each spawn window
    /// belonged to and the rewrite is lossless rather than a guess. A window that recorded no owner
    /// keeps that: it becomes the unowned form, which is exactly what the client produces for a spawn
    /// window nobody owns, so it too stays reachable.
    /// </para>
    /// <para>
    /// <b>The decision is made by version, never by looking at the id.</b> A target is free text and may
    /// hold anything — including something that reads like a v5 id — so classifying ids by shape would
    /// eventually mis-file one and produce the orphan pane this step exists to prevent. Everything under
    /// a document that says version 4 is v4, by construction.
    /// </para>
    /// </summary>
    private static void MigrateV4ToV5(JsonObject root)
    {
        if (root["lastSession"] is not JsonObject session || session["windows"] is not JsonArray windows)
        {
            return;
        }

        var rewritten = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var window in windows.OfType<JsonObject>())
        {
            // Kind is written by the string enum converter, so it is the member name and not a number.
            if (!string.Equals(Text(window["kind"]), nameof(Workspaces.WindowKind.Spawn),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Text(window["id"]) is not { } id
                || !id.StartsWith(Workspaces.Workspace.SpawnPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var target = id[Workspaces.Workspace.SpawnPrefix.Length..];
            if (target.Length == 0)
            {
                continue;
            }

            var upgraded = Workspaces.Workspace.SpawnWindowId(Text(window["sessionKey"]), target);
            window["id"] = upgraded;
            rewritten[id] = upgraded;
        }

        if (rewritten.Count > 0 && session["root"] is JsonObject layout)
        {
            RewriteTabs(layout, rewritten);
        }
    }

    /// <summary>Re-points every pane's tab list at the ids <see cref="MigrateV4ToV5"/> rewrote.</summary>
    private static void RewriteTabs(JsonObject node, Dictionary<string, string> rewritten)
    {
        if (node["tabs"] is JsonArray tabs)
        {
            for (var i = 0; i < tabs.Count; i++)
            {
                if (Text(tabs[i]) is { } tab && rewritten.TryGetValue(tab, out var upgraded))
                {
                    tabs[i] = upgraded;
                }
            }
        }

        if (node["children"] is JsonArray children)
        {
            foreach (var child in children.OfType<JsonObject>())
            {
                RewriteTabs(child, rewritten);
            }
        }
    }

    /// <summary>
    /// A node's string value, or null when it is absent or is not a string. A hand-edited or
    /// hand-truncated document must not stop the client starting, and <c>GetValue&lt;string&gt;</c>
    /// throws on a number where this returns null.
    /// </summary>
    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    /// v3's <c>autoLogin</c> is gone: whether a character logs itself in is now derived from whether it
    /// has anything to send (<see cref="CharacterDefinition.Login"/>, <see cref="LoginPlan"/>). The
    /// property is removed here, and the one case that would otherwise lose behaviour is written out
    /// explicitly.
    /// <para>
    /// <b><c>autoLogin: false</c> is discarded, deliberately, and that changes what some configs do.</b>
    /// It is not a decision anyone made: a <c>bool</c> serializes on every character whether or not it was
    /// ever touched, so the overwhelming majority of the <c>false</c>s on disk are the default nobody
    /// chose. Honouring them would preserve exactly the trap this change removes — a saved password with
    /// that flag at its default is a credential that silently does nothing — so a character with a stored
    /// password now logs in. That is the fix, stated as a migration rather than left for the user to
    /// discover.
    /// </para>
    /// <para>
    /// <b><c>autoLogin: true</c> with nothing to send is preserved by writing the line it was sending.</b>
    /// That combination — the flag on, no password, no connect line of its own — resolved to
    /// <c>connect &lt;Name&gt;</c> through the default template's empty-value rule, and it is a real
    /// configuration on a passwordless world. Under the new rule a character with neither a password nor a
    /// connect line sends nothing, so the migration gives it the line: <c>connect %CHARACTER%</c>, which
    /// resolves to the identical string. It is written rather than inferred because the alternative is
    /// invisible state — two characters showing the same greyed default on F5, one sending and one not.
    /// </para>
    /// </summary>
    private static void MigrateV3ToV4(JsonObject root)
    {
        if (root["worlds"] is not JsonArray worlds)
        {
            return;
        }

        foreach (var character in worlds.OfType<JsonObject>()
                     .Select(w => w["characters"] as JsonArray)
                     .Where(c => c is not null)
                     .SelectMany(c => c!.OfType<JsonObject>()))
        {
            var autoLogin = character["autoLogin"]?.GetValue<bool>() ?? false;
            character.Remove("autoLogin");

            if (!autoLogin)
            {
                continue;
            }

            // A password (a passwordRef is the only trace of one in this document) or a connect line of
            // its own already says "log in" under the new rule; there is nothing to write.
            var hasPassword = character["passwordRef"] is not null;
            var hasOwnLine = !string.IsNullOrWhiteSpace(character["connectString"]?.GetValue<string>());
            if (hasPassword || hasOwnLine)
            {
                continue;
            }

            character["connectString"] = "connect " + ConnectStringTemplate.CharacterToken;
        }
    }

    /// <summary>
    /// v2's world <c>encoding</c> was a preference the client stated and then ignored; in v3 it is an
    /// <em>override</em> of what CHARSET negotiates, with <c>auto</c> as the default. Every v2 file
    /// carries an explicit <c>UTF-8</c> because that was the default nobody chose, so leaving it would
    /// pin every existing world to an override for ever and defeat the feature entirely. It becomes
    /// <c>auto</c>. Any other name was typed on the F5 screen deliberately and is kept as the override
    /// it now means.
    /// </summary>
    private static void MigrateV2ToV3(JsonObject root)
    {
        if (root["worlds"] is not JsonArray worlds)
        {
            return;
        }

        foreach (var world in worlds.OfType<JsonObject>())
        {
            if (world["encoding"]?.GetValue<string>() is { } encoding &&
                encoding.Trim().Equals("utf-8", StringComparison.OrdinalIgnoreCase))
            {
                world["encoding"] = Telnet.TelnetSessionOptions.AutoEncodingName;
            }
        }
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

        if (world["characters"] is JsonArray characters)
        {
            // Partially-migrated file: wire the new set into the existing characters so the
            // freshly-lifted automation isn't orphaned in an unreferenced set.
            if (setName is not null)
            {
                foreach (var characterNode in characters.OfType<JsonObject>())
                {
                    if (characterNode["triggerSets"] is JsonArray existing)
                    {
                        existing.Add(setName);
                    }
                    else
                    {
                        characterNode["triggerSets"] = new JsonArray(setName);
                    }
                }
            }
        }
        else
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
