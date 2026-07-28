using System.Globalization;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Text;
using static SharpMUTerm.Tui.MarkupText;
using static SharpMUTerm.Tui.ScreenPalette;

namespace SharpMUTerm.Tui;

/// <summary>
/// Produces the markup sub-blocks for the F5 Worlds &amp; Characters screen — the header band, the
/// WORLDS list, the world/characters detail column, the character form, the assigned-trigger-set
/// list, and the footer action bar. <see cref="WorldsScreenView"/> composes these into real panels
/// (grids) for the live/snapshot view; <see cref="Render"/> merges the same blocks into a single
/// line list for the unit tests. Pure so every block is testable.
/// </summary>
internal static class WorldsScreenRenderer
{
    private const int LeftColumnWidth = 28;
    private const int WorldLabelWidth = 9;
    private const int CharLabelWidth = 10;
    private const int CharDetailColumnWidth = 40;
    private const string DividerGlyph = " │ ";

    /// <summary>The accent hex for the selected world (its own, or the default teal).</summary>
    internal static string AccentFor(IReadOnlyList<WorldDefinition> worlds, int selectedWorld) =>
        selectedWorld >= 0 && selectedWorld < worlds.Count ? Hex(worlds[selectedWorld].Accent) : Accent;

    /// <summary>
    /// Merges every sub-block into one line list (header, worlds list | detail, character form |
    /// trigger sets, footer). Used by the unit tests and as a width-agnostic fallback; the live view
    /// composes the same blocks into panels instead. <paramref name="width"/>/<paramref name="height"/>
    /// &gt; 0 lay the merged form out full-console (bands + footer at the last row).
    /// </summary>
    public static List<string> Render(
        IReadOnlyList<WorldDefinition> worlds,
        IReadOnlyList<TriggerSet> triggerSets,
        int selectedWorld,
        int selectedCharacter,
        int width = 0,
        int height = 0)
    {
        ArgumentNullException.ThrowIfNull(worlds);
        ArgumentNullException.ThrowIfNull(triggerSets);

        var accent = AccentFor(worlds, selectedWorld);
        var lines = new List<string> { Band(HeaderLine(width), HeaderBg, width) };
        lines.AddRange(MergeColumns(WorldsColumn(worlds, selectedWorld),
            DetailColumn(worlds, triggerSets, selectedWorld, selectedCharacter, accent), LeftColumnWidth));

        if (HasCharacter(worlds, selectedWorld, selectedCharacter))
        {
            var character = worlds[selectedWorld].Characters[selectedCharacter];
            lines.Add(string.Empty);
            lines.Add(Band($"[{Rule}]{new string('─', width > 4 ? width - 2 : 60)}[/]", EditBg, width));
            foreach (var row in MergeColumns(FormColumn(character, accent),
                TriggersColumn(character, triggerSets, accent), CharDetailColumnWidth))
            {
                lines.Add(Band(" " + row, EditBg, width));
            }
        }

        if (height > 0)
        {
            while (lines.Count < height - 1)
            {
                lines.Add(string.Empty);
            }
        }

        lines.Add(Band(FooterLine(worlds, selectedWorld, selectedCharacter, accent, width), FooterBg, width));
        return lines;
    }

    internal static bool HasCharacter(IReadOnlyList<WorldDefinition> worlds, int selectedWorld, int selectedCharacter) =>
        selectedWorld >= 0 && selectedWorld < worlds.Count &&
        selectedCharacter >= 0 && selectedCharacter < worlds[selectedWorld].Characters.Count;

    internal static string HeaderLine(int width)
    {
        var title = $"[bold {Value}] Worlds & Characters[/]";
        var hints = ScreenChrome.Hints("↑↓ select · ⇥ switch pane · ⏎ edit", "F5");
        return SpreadLR(" " + title, hints, width);
    }

    internal static string FooterLine(
        IReadOnlyList<WorldDefinition> worlds, int selectedWorld, int selectedCharacter, string accent, int width)
    {
        var context = string.Empty;
        if (worlds.Count > 0 && selectedWorld >= 0)
        {
            context = $"[{Label}]world {selectedWorld + 1}/{worlds.Count}[/]";
            var chars = worlds[selectedWorld].Characters.Count;
            if (chars > 0 && selectedCharacter >= 0)
            {
                context += $"[{Label}]  ·  character {selectedCharacter + 1}/{chars}[/]";
            }
        }

        var actions = ScreenChrome.Actions(accent);
        return SpreadLR(" " + context, actions, width);
    }

    internal static List<string> WorldsColumn(IReadOnlyList<WorldDefinition> worlds, int selectedWorld)
    {
        var left = new List<string> { $"[{Label}]WORLDS[/]", string.Empty };

        for (var i = 0; i < worlds.Count; i++)
        {
            if (i > 0)
            {
                left.Add(string.Empty);
            }

            var world = worlds[i];
            var selected = i == selectedWorld;
            var marker = selected ? $"[bold {Accent}]▸[/]" : " ";
            var accentHex = Hex(world.Accent);
            var name = selected ? $"[bold {Value}]{Escape(world.Name)}[/]" : $"[{Value}]{Escape(world.Name)}[/]";
            left.Add($"{marker} [{accentHex}]▚[/] {name}");
            left.Add($"    [{Label}]{Escape(world.Host)}:{world.Port.ToString(CultureInfo.InvariantCulture)}[/]");
            left.Add($"    [{Label}]{world.Characters.Count.ToString(CultureInfo.InvariantCulture)} chars[/]");
        }

        if (worlds.Count == 0)
        {
            left.Add($"[{Label}]no worlds[/]");
        }

        left.Add(string.Empty);
        left.Add($"[{Accent}][[+ world]][/]  [{Label}][[- del]][/]");
        return left;
    }

    internal static List<string> DetailColumn(
        IReadOnlyList<WorldDefinition> worlds,
        IReadOnlyList<TriggerSet> triggerSets,
        int selectedWorld,
        int selectedCharacter,
        string accent)
    {
        if (selectedWorld < 0 || selectedWorld >= worlds.Count)
        {
            return new List<string>();
        }

        var world = worlds[selectedWorld];
        var right = new List<string>
        {
            $"[bold {Value}]{Escape(world.Name)}[/]  [{Label}]{Escape(world.Host)}:{world.Port.ToString(CultureInfo.InvariantCulture)}[/]"
            + $"  [{accent}]TLS {OnOff(world.UseTls)}[/][{Label}] · {Escape(world.Encoding)}[/]",
            string.Empty,
            $"[{accent}]├ WORLD[/]",
            WorldField("name", $"[{Value}]{Escape(world.Name)}[/]"),
            WorldField("host", $"[{Value}]{Escape(world.Host)}[/]"),
            WorldField("port", $"[{Value}]{world.Port.ToString(CultureInfo.InvariantCulture)}[/]"),
            WorldField("security", $"[{Value}]{Security(world)}[/]"),
            WorldField("encoding", $"[{Value}]{Escape(world.Encoding)}[/]"),
            WorldField("keepalive", world.KeepaliveSeconds > 0
                ? $"[{Value}]{world.KeepaliveSeconds.ToString(CultureInfo.InvariantCulture)}s[/]"
                : $"[{Label}]off[/]"),
            string.Empty,
            $"[{accent}]├ CHARACTERS[/]   [{Label}]a character is a connection[/]",
            $"[{Label}]  name          state       login        trigger sets[/]",
        };

        if (world.Characters.Count == 0)
        {
            right.Add($"[{Label}]no characters — this world has nothing to connect with.[/]");
        }
        else
        {
            for (var i = 0; i < world.Characters.Count; i++)
            {
                right.Add(CharacterRow(world.Characters[i], i == selectedCharacter));
            }
        }

        right.Add(string.Empty);
        right.Add($"[{Accent}][[+ add character]][/] [{Label}][[⧉ duplicate]] [[- remove]][/]");
        return right;
    }

    /// <summary>The character form — labels left-aligned with their values, one field per row.</summary>
    internal static List<string> FormColumn(CharacterDefinition character, string accent) => new()
    {
        $"[bold {accent}]└ CHARACTER · {Escape(character.Name)}[/]",
        string.Empty,
        CharField("name", $"[{Value}]{Escape(character.Name)}[/]"),
        CharField("password", $"[{Value}]••••••••[/] [{Label}]keychain[/]"),
        CharField("on connect", $"[{Value}]{Escape(character.OnConnect ?? "—")}[/]"),
        CharField("auto-login", character.AutoLogin ? $"[{accent}]yes[/]" : $"[{Label}]no[/]"),
        CharField("session", $"[{Label}]offline[/]"),
    };

    /// <summary>The assigned-trigger-sets checklist for a character.</summary>
    internal static List<string> TriggersColumn(
        CharacterDefinition character, IReadOnlyList<TriggerSet> triggerSets, string accent)
    {
        var list = new List<string> { $"[{Label}]assigned trigger sets[/]", string.Empty };
        foreach (var set in triggerSets)
        {
            var assigned = character.TriggerSets.Contains(set.Name);
            var box = assigned ? $"[{accent}][[x]][/]" : $"[{Label}][[ ]][/]";
            var nameColor = assigned ? Value : Label;
            var description = Escape(set.Description ?? string.Empty);
            list.Add(
                $"{box} [{nameColor}]▪ {Escape(set.Name)}[/] [{Label}]— {description}   {set.Triggers.Count.ToString(CultureInfo.InvariantCulture)} rules[/]");
        }

        return list;
    }

    private static string CharacterRow(CharacterDefinition character, bool selected)
    {
        var marker = selected ? $"[bold {Accent}]▸[/]" : " ";
        var name = PadVisible($"[{(selected ? "bold " : string.Empty)}{Value}]{Escape(character.Name)}[/]", 13);
        var login = PadVisible(character.AutoLogin ? "auto-login" : "manual", 12);
        var sets = Escape(string.Join(", ", character.TriggerSets));
        return $"{marker} {name} [{Label}]○ offline[/]  [{Label}]{login}[/] [{Label}]{sets}[/]";
    }

    private static string Security(WorldDefinition world) =>
        world.UseTls
            ? $"TLS on · certs {(world.AllowInvalidCertificates ? "lax" : "strict")}"
            : "TLS off";

    private static string WorldField(string label, string value) =>
        $"  [{Label}]{label.PadLeft(WorldLabelWidth)}[/]  {value}";

    private static string CharField(string label, string value) =>
        $"  [{Label}]{label.PadRight(CharLabelWidth)}[/]  {value}";

    private static string OnOff(bool value) => value ? "on" : "off";

    private static string Band(string inner, string bg, int width)
    {
        if (width <= 0)
        {
            return inner;
        }

        var pad = Math.Max(0, width - VisibleLength(inner));
        return $"[on {bg}]{inner}{new string(' ', pad)}[/]";
    }

    private static List<string> MergeColumns(IReadOnlyList<string> left, IReadOnlyList<string> right, int leftWidth)
    {
        var count = Math.Max(left.Count, right.Count);
        var merged = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var l = i < left.Count ? left[i] : string.Empty;
            var r = i < right.Count ? right[i] : string.Empty;
            merged.Add(PadVisible(l, leftWidth) + $"[{Rule}]{DividerGlyph}[/]" + r);
        }

        return merged;
    }

    private static string Hex(TerminalColor color) =>
        color.Kind == TerminalColorKind.Rgb ? $"#{color.R:x2}{color.G:x2}{color.B:x2}" : Accent;
}
