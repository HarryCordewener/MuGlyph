using SharpMUTerm.Core.Text;
using SharpMUTerm.Core.Theming;
using SharpMUTerm.Core.Workspaces;

namespace SharpMUTerm.Core.Configuration;

/// <summary>Top-level SharpMUTerm configuration: global preferences, saved worlds, and shared trigger sets.</summary>
public sealed class AppConfiguration
{
    /// <summary>The current on-disk schema version. Older configs are upgraded by <see cref="ConfigurationMigrator"/>.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Schema version, for future migrations.</summary>
    public int Version { get; set; } = CurrentVersion;

    /// <summary>The name of the active built-in theme (see <see cref="ThemeLibrary"/>).</summary>
    public string ThemeName { get; set; } = "Dark";

    /// <summary>
    /// The active theme's colours. Defaults to the dark theme; editing these fields customises
    /// the theme in place. Set <see cref="ThemeName"/> to a built-in name to reset.
    /// </summary>
    public Theme Theme { get; set; } = ThemeLibrary.Dark();

    /// <summary>
    /// Maximum scrollback lines retained per session. See
    /// <see cref="SharpMUTerm.Core.Text.ScrollbackBuffer.DefaultCapacity"/> for why the default is ten
    /// thousand and not the twenty thousand it used to be. (Fully qualified: this class has its own
    /// <see cref="Text"/> property, which shadows the namespace inside it.)
    /// </summary>
    public int ScrollbackLines { get; set; } = SharpMUTerm.Core.Text.ScrollbackBuffer.DefaultCapacity;

    /// <summary>
    /// How much history beyond <see cref="ScrollbackLines"/> is kept in a per-session disk cache so
    /// the view can scroll further back than memory holds. An ephemeral cache, deleted when the
    /// session closes — not a session transcript, which is the separate, opt-in
    /// <c>PlainTextLogSink</c>/<c>HtmlLogSink</c> feature.
    /// </summary>
    public ScrollbackSpillOptions ScrollbackSpill { get; set; } = new();

    /// <summary>
    /// Forces a graphics protocol regardless of capability detection: one of
    /// <c>none</c>, <c>halfblock</c>, <c>sixel</c>, <c>kitty</c>. Null means auto-detect.
    /// </summary>
    public string? GraphicsOverride { get; set; }

    /// <summary>Default charset preference order (IANA names), most-preferred first.</summary>
    public List<string> CharsetOrder { get; set; } = new() { "utf-8", "iso-8859-1" };

    /// <summary>How inbound text is drawn — the F7 "Text &amp; ANSI" screen's settings.</summary>
    public TextSettings Text { get; set; } = new();

    /// <summary>How the command line behaves — the F8 "Input &amp; spellcheck" screen's settings.</summary>
    public InputSettings Input { get; set; } = new();

    /// <summary>The saved worlds (servers), each holding its own characters.</summary>
    public List<WorldDefinition> Worlds { get; set; } = new();

    /// <summary>
    /// World-independent automation bundles. Characters opt in by name via
    /// <see cref="CharacterDefinition.TriggerSets"/>, so a set can be shared across worlds and
    /// characters. See <see cref="ResolveTriggerSets"/>.
    /// </summary>
    public List<TriggerSet> TriggerSets { get; set; } = new();

    /// <summary>
    /// The last workspace layout (panes, windows, focus) so the app can resume where it left off.
    /// Null on a fresh config; the shell rebuilds a live workspace from it at startup via
    /// <see cref="WorkspaceState.Restore"/>. Scrollback is not persisted — only structure.
    /// </summary>
    public WorkspaceState? LastSession { get; set; }

    /// <summary>
    /// Resolves the <see cref="TriggerSet"/>s a character has opted into, in the character's own
    /// order, matching by name case-insensitively. Names with no matching set are skipped, and
    /// each set is returned at most once even if referenced twice.
    /// </summary>
    public IReadOnlyList<TriggerSet> ResolveTriggerSets(CharacterDefinition character)
    {
        ArgumentNullException.ThrowIfNull(character);

        var byName = new Dictionary<string, TriggerSet>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in TriggerSets)
        {
            byName.TryAdd(set.Name, set);
        }

        var resolved = new List<TriggerSet>();
        var seen = new HashSet<TriggerSet>();
        foreach (var name in character.TriggerSets)
        {
            if (byName.TryGetValue(name, out var set) && seen.Add(set))
            {
                resolved.Add(set);
            }
        }

        return resolved;
    }
}
