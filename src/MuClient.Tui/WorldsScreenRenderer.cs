using System.Globalization;
using MuClient.Core.Configuration;
using MuClient.Core.Text;

namespace MuClient.Tui;

/// <summary>
/// Renders the F5 Worlds &amp; Characters screen: a left WORLDS list and a right detail column
/// (world fields, a characters table, and the selected character's edit block) merged into
/// single full-width markup lines. Pure so the screen is unit-testable; the modal host just
/// displays what this produces.
/// </summary>
internal static class WorldsScreenRenderer
{
    private const string DefaultAccent = "#00f5b7";
    private const int LeftColumnWidth = 26;
    private const int WorldLabelWidth = 9;
    private const int CharLabelWidth = 10;
    private const int CharDetailColumnWidth = 24;
    private const string Divider = " │ ";

    public static List<string> Render(
        IReadOnlyList<WorldDefinition> worlds,
        IReadOnlyList<TriggerSet> triggerSets,
        int selectedWorld,
        int selectedCharacter)
    {
        ArgumentNullException.ThrowIfNull(worlds);
        ArgumentNullException.ThrowIfNull(triggerSets);

        var lines = new List<string>
        {
            "[dim]‹ back[/]   [bold]Worlds & Characters[/]   [dim]F5[/]",
        };

        var left = BuildLeft(worlds, selectedWorld);
        var right = BuildRight(worlds, triggerSets, selectedWorld, selectedCharacter);
        lines.AddRange(MergeColumns(left, right, LeftColumnWidth));

        lines.Add("[dim][[Cancel]]   [[Save]][/]");
        return lines;
    }

    private static List<string> BuildLeft(IReadOnlyList<WorldDefinition> worlds, int selectedWorld)
    {
        var left = new List<string> { "[dim]┌ WORLDS[/]" };

        for (var i = 0; i < worlds.Count; i++)
        {
            if (i > 0)
            {
                left.Add(string.Empty);
            }

            var world = worlds[i];
            var marker = i == selectedWorld ? "[bold]▸[/]" : " ";
            var accentHex = Hex(world.Accent);
            left.Add($"{marker} [{accentHex}]▚[/] [bold]{Escape(world.Name)}[/]");
            left.Add($"    [dim]{Escape(world.Host)}:{world.Port.ToString(CultureInfo.InvariantCulture)}[/]");
            left.Add($"    [dim]{world.Characters.Count.ToString(CultureInfo.InvariantCulture)} chars[/]");
        }

        if (worlds.Count == 0)
        {
            left.Add("[dim]no worlds[/]");
        }

        left.Add(string.Empty);
        left.Add("[dim][[+ world]]  [[- del]][/]");
        return left;
    }

    private static List<string> BuildRight(
        IReadOnlyList<WorldDefinition> worlds,
        IReadOnlyList<TriggerSet> triggerSets,
        int selectedWorld,
        int selectedCharacter)
    {
        if (selectedWorld < 0 || selectedWorld >= worlds.Count)
        {
            return new List<string>();
        }

        var world = worlds[selectedWorld];
        var right = new List<string>
        {
            $"[bold]{Escape(world.Name)}[/]  [dim]{Escape(world.Host)}:{world.Port.ToString(CultureInfo.InvariantCulture)}[/]"
            + $"  [dim]TLS {OnOff(world.UseTls)}[/]  [dim]{Escape(world.Encoding)}[/]",
            string.Empty,
            "[dim]├ WORLD[/]",
            WorldField("name", Escape(world.Name)),
            WorldField("host", Escape(world.Host)),
            WorldField("port", world.Port.ToString(CultureInfo.InvariantCulture)),
            WorldField("security", Security(world)),
            WorldField("encoding", Escape(world.Encoding)),
            WorldField("keepalive", world.KeepaliveSeconds > 0
                ? $"{world.KeepaliveSeconds.ToString(CultureInfo.InvariantCulture)}s"
                : "off"),
            string.Empty,
            "[dim]├ CHARACTERS[/]   [dim]a character is a connection[/]",
            "[dim]  name          state       login        trigger sets[/]",
        };

        if (world.Characters.Count == 0)
        {
            right.Add("[dim]no characters — this world has nothing to connect with.[/]");
        }
        else
        {
            for (var i = 0; i < world.Characters.Count; i++)
            {
                right.Add(CharacterRow(world.Characters[i], i == selectedCharacter));
            }
        }

        right.Add(string.Empty);
        right.Add("[dim][[+ add character]] [[⧉ duplicate]] [[- remove]][/]");

        if (world.Characters.Count > 0 && selectedCharacter >= 0 && selectedCharacter < world.Characters.Count)
        {
            right.Add(string.Empty);
            right.AddRange(BuildCharacterDetail(world.Characters[selectedCharacter], triggerSets, Hex(world.Accent)));
        }

        return right;
    }

    private static string CharacterRow(CharacterDefinition character, bool selected)
    {
        var marker = selected ? "[bold]▸[/]" : " ";
        var name = PadVisible(Escape(character.Name), 13);
        var login = (character.AutoLogin ? "auto-login" : "manual").PadRight(12);
        var sets = Escape(string.Join(", ", character.TriggerSets));
        return $"{marker} {name} [dim]○ offline[/] {login} {sets}";
    }

    private static List<string> BuildCharacterDetail(
        CharacterDefinition character,
        IReadOnlyList<TriggerSet> triggerSets,
        string accentHex)
    {
        var detail = new List<string> { $"[dim]└ CHARACTER · {Escape(character.Name)}[/]" };

        var charLeft = new List<string>
        {
            CharField("name", Escape(character.Name)),
            CharField("password", "•••••••• [dim]keychain[/]"),
            CharField("on connect", Escape(character.OnConnect ?? "—")),
            CharField("auto-login", character.AutoLogin ? "yes" : "no"),
            CharField("session", "[dim]offline[/]"),
        };

        var charRight = new List<string> { "[dim]assigned trigger sets[/]" };
        foreach (var set in triggerSets)
        {
            var box = character.TriggerSets.Contains(set.Name) ? $"[{accentHex}][[x]][/]" : "[dim][[ ]][/]";
            var description = Escape(set.Description ?? string.Empty);
            charRight.Add(
                $"{box} ▪ {Escape(set.Name)} — {description}   [dim]{set.Triggers.Count.ToString(CultureInfo.InvariantCulture)} rules[/]");
        }

        detail.AddRange(MergeColumns(charLeft, charRight, CharDetailColumnWidth));
        return detail;
    }

    private static string Security(WorldDefinition world) =>
        world.UseTls
            ? $"TLS on · certs {(world.AllowInvalidCertificates ? "lax" : "strict")}"
            : "TLS off";

    private static string WorldField(string label, string value) =>
        $"  [dim]{label.PadLeft(WorldLabelWidth)}[/]  {value}";

    private static string CharField(string label, string value) =>
        $"  [dim]{label.PadLeft(CharLabelWidth)}[/]  {value}";

    private static string OnOff(bool value) => value ? "on" : "off";

    /// <summary>Merges two column line-lists into single lines, blank-padding the shorter side.</summary>
    private static List<string> MergeColumns(IReadOnlyList<string> left, IReadOnlyList<string> right, int leftWidth)
    {
        var count = Math.Max(left.Count, right.Count);
        var merged = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var l = i < left.Count ? left[i] : string.Empty;
            var r = i < right.Count ? right[i] : string.Empty;
            merged.Add(PadVisible(l, leftWidth) + Divider + r);
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
