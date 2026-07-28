using System.Globalization;
using MuClient.Core.Configuration;
using MuClient.Core.Text;

namespace MuClient.Tui;

/// <summary>
/// Renders the F5 Worlds &amp; Characters screen: a top-left WORLDS list beside a world/characters
/// detail column, over a full-width CHARACTER editing pane (its own elevated background), framed by
/// a header band (title + keyboard hints) and a footer action bar (Cancel/Save) pinned to the
/// bottom. Pure so the screen is unit-testable. Passing <c>width</c>/<c>height</c> (&gt; 0) lays the
/// screen out to fill the console — full-width bands, the editing-pane background, and the footer at
/// the last row; with both 0 it renders the same content in natural width (used by the unit tests).
/// </summary>
internal static class WorldsScreenRenderer
{
    private const string DefaultAccent = "#00f5b7";
    private const int LeftColumnWidth = 28;
    private const int WorldLabelWidth = 9;
    private const int CharLabelWidth = 10;
    private const int CharDetailColumnWidth = 26;
    private const string Divider = " │ ";

    // Palette. The panel background is set on the host control; these are the accents/bands drawn in markup.
    private const string HeaderBg = "#232b3d";
    private const string EditBg = "#1d2333";
    private const string FooterBg = "#232b3d";
    private const string Label = "#7c8699";
    private const string Value = "#d7deec";
    private const string RuleColor = "#3a4257";
    private const string SelectBg = "#2c3448";
    private const string Ink = "#0f1620";

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

        var accent = selectedWorld >= 0 && selectedWorld < worlds.Count ? Hex(worlds[selectedWorld].Accent) : DefaultAccent;

        var lines = new List<string> { HeaderBand(width) };

        var left = BuildLeft(worlds, selectedWorld);
        var right = BuildRight(worlds, triggerSets, selectedWorld, selectedCharacter, accent);
        lines.AddRange(MergeColumns(left, right, LeftColumnWidth, width));

        // The character editing pane is a full-width, distinctly-backed band below the two-column area.
        if (selectedWorld >= 0 && selectedWorld < worlds.Count &&
            selectedCharacter >= 0 && selectedCharacter < worlds[selectedWorld].Characters.Count)
        {
            lines.Add(string.Empty);
            lines.AddRange(EditPane(worlds[selectedWorld].Characters[selectedCharacter], triggerSets, accent, width));
        }

        // Footer action bar pinned to the last row (pad the panel out to it when a height is given).
        if (height > 0)
        {
            while (lines.Count < height - 1)
            {
                lines.Add(string.Empty);
            }
        }

        lines.Add(FooterBand(worlds, selectedWorld, selectedCharacter, accent, width));
        return lines;
    }

    private static string HeaderBand(int width)
    {
        var title = $"[bold {Value}] Worlds & Characters[/]";
        var hints = $"[{Label}]↑↓ select · ⇥ switch pane · ⏎ edit · [/][{DefaultAccent}]Esc[/][{Label}] close[/]";
        return Band(SpreadLR(" " + title, hints + " ", width), HeaderBg, width);
    }

    private static string FooterBand(
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

        var actions = $"[{Label}] [[Esc]] cancel [/]  [{Ink} on {accent}] [[⏎]] Save [/] ";
        return Band(SpreadLR(" " + context, actions, width), FooterBg, width);
    }

    private static List<string> BuildLeft(IReadOnlyList<WorldDefinition> worlds, int selectedWorld)
    {
        var left = new List<string> { $"[{Label}]WORLDS[/]" };

        for (var i = 0; i < worlds.Count; i++)
        {
            if (i > 0)
            {
                left.Add(string.Empty);
            }

            var world = worlds[i];
            var selected = i == selectedWorld;
            var marker = selected ? $"[bold {DefaultAccent}]▸[/]" : " ";
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
        left.Add($"[{DefaultAccent}][[+ world]][/]  [{Label}][[- del]][/]");
        return left;
    }

    private static List<string> BuildRight(
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
        right.Add($"[{DefaultAccent}][[+ add character]][/] [{Label}][[⧉ duplicate]] [[- remove]][/]");
        return right;
    }

    private static string CharacterRow(CharacterDefinition character, bool selected)
    {
        var marker = selected ? $"[bold {DefaultAccent}]▸[/]" : " ";
        var name = PadVisible($"[{(selected ? "bold " : string.Empty)}{Value}]{Escape(character.Name)}[/]", 13);
        var login = PadVisible(character.AutoLogin ? "auto-login" : "manual", 12);
        var sets = Escape(string.Join(", ", character.TriggerSets));
        return $"{marker} {name} [{Label}]○ offline[/]  [{Label}]{login}[/] [{Label}]{sets}[/]";
    }

    private static List<string> EditPane(
        CharacterDefinition character,
        IReadOnlyList<TriggerSet> triggerSets,
        string accent,
        int width)
    {
        var charLeft = new List<string>
        {
            $"[bold {accent}]└ CHARACTER · {Escape(character.Name)}[/]",
            string.Empty,
            CharField("name", $"[{Value}]{Escape(character.Name)}[/]"),
            CharField("password", $"[{Value}]••••••••[/] [{Label}]keychain[/]"),
            CharField("on connect", $"[{Value}]{Escape(character.OnConnect ?? "—")}[/]"),
            CharField("auto-login", character.AutoLogin ? $"[{accent}]yes[/]" : $"[{Label}]no[/]"),
            CharField("session", $"[{Label}]offline[/]"),
        };

        var charRight = new List<string>
        {
            string.Empty,
            string.Empty,
            $"[{Label}]assigned trigger sets[/]",
        };
        foreach (var set in triggerSets)
        {
            var assigned = character.TriggerSets.Contains(set.Name);
            var box = assigned ? $"[{accent}][[x]][/]" : $"[{Label}][[ ]][/]";
            var nameColor = assigned ? Value : Label;
            var description = Escape(set.Description ?? string.Empty);
            charRight.Add(
                $"{box} [{nameColor}]▪ {Escape(set.Name)}[/] [{Label}]— {description}   {set.Triggers.Count.ToString(CultureInfo.InvariantCulture)} rules[/]");
        }

        // A subtle rule opens the editing pane, then its rows carry the elevated edit background.
        var pane = new List<string> { Band($"[{RuleColor}]{Rule(width)}[/]", EditBg, width) };
        foreach (var row in MergeColumns(charLeft, charRight, CharDetailColumnWidth, 0))
        {
            pane.Add(Band(" " + row, EditBg, width));
        }

        return pane;
    }

    private static string Security(WorldDefinition world) =>
        world.UseTls
            ? $"TLS on · certs {(world.AllowInvalidCertificates ? "lax" : "strict")}"
            : "TLS off";

    private static string WorldField(string label, string value) =>
        $"  [{Label}]{label.PadLeft(WorldLabelWidth)}[/]  {value}";

    private static string CharField(string label, string value) =>
        $"  [{Label}]{label.PadLeft(CharLabelWidth)}[/]  {value}";

    private static string OnOff(bool value) => value ? "on" : "off";

    private static string Rule(int width) => new('─', width > 4 ? width - 2 : 40);

    /// <summary>
    /// Lays out a left- and right-hand markup fragment on one line, right-aligning the right fragment
    /// to <paramref name="width"/> (or a single gap in natural mode when width is 0).
    /// </summary>
    private static string SpreadLR(string left, string right, int width)
    {
        if (width <= 0)
        {
            return $"{left}   {right}";
        }

        var gap = Math.Max(1, width - VisibleLength(left) - VisibleLength(right));
        return left + new string(' ', gap) + right;
    }

    /// <summary>Wraps a row as a full-width background band (padded to <paramref name="width"/>); a no-op when width is 0.</summary>
    private static string Band(string inner, string bg, int width)
    {
        if (width <= 0)
        {
            return inner;
        }

        var pad = Math.Max(0, width - VisibleLength(inner));
        return $"[on {bg}]{inner}{new string(' ', pad)}[/]";
    }

    /// <summary>Merges two column line-lists into single lines, blank-padding the shorter side.</summary>
    private static List<string> MergeColumns(IReadOnlyList<string> left, IReadOnlyList<string> right, int leftWidth, int width)
    {
        var count = Math.Max(left.Count, right.Count);
        var merged = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var l = i < left.Count ? left[i] : string.Empty;
            var r = i < right.Count ? right[i] : string.Empty;
            merged.Add(PadVisible(l, leftWidth) + $"[{RuleColor}]{Divider}[/]" + r);
        }

        return merged;
    }

    /// <summary>Pads a markup string with trailing spaces to <paramref name="width"/> visible columns,
    /// ignoring markup tags (but counting escaped <c>[[</c>/<c>]]</c> literals as one column each).</summary>
    private static string PadVisible(string markup, int width)
    {
        var visible = VisibleLength(markup);
        return visible >= width ? markup : markup + new string(' ', width - visible);
    }

    private static int VisibleLength(string markup)
    {
        var length = 0;
        var i = 0;
        while (i < markup.Length)
        {
            var c = markup[i];
            if (c == '[')
            {
                if (i + 1 < markup.Length && markup[i + 1] == '[')
                {
                    length++;
                    i += 2;
                    continue;
                }

                var close = markup.IndexOf(']', i);
                if (close < 0)
                {
                    length += markup.Length - i;
                    break;
                }

                i = close + 1;
                continue;
            }

            if (c == ']' && i + 1 < markup.Length && markup[i + 1] == ']')
            {
                length++;
                i += 2;
                continue;
            }

            length++;
            i++;
        }

        return length;
    }

    private static string Hex(TerminalColor color) =>
        color.Kind == TerminalColorKind.Rgb ? $"#{color.R:x2}{color.G:x2}{color.B:x2}" : DefaultAccent;

    private static string Escape(string text) => text.Replace("[", "[[").Replace("]", "]]");
}
