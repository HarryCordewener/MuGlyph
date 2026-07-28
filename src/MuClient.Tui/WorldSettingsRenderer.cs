using System.Globalization;
using MuClient.Core.Configuration;
using MuClient.Core.Text;

namespace MuClient.Tui;

/// <summary>
/// Renders a world's settings into markup lines for the settings menu: connection, rendering, and
/// character sections laid out as aligned key/value rows with an accent swatch. Pure so the panel is
/// unit-testable; the modal host just displays what this produces.
/// </summary>
internal static class WorldSettingsRenderer
{
    private const string Accent = "#00f5b7";
    private const int LabelWidth = 14;

    public static List<string> Render(WorldDefinition world, TerminalColor accent)
    {
        ArgumentNullException.ThrowIfNull(world);
        var accentHex = Hex(accent);

        var lines = new List<string>
        {
            $"[bold {accentHex}]⚙ WORLD SETTINGS[/]   [dim]{Escape(world.Name)}[/]",
            string.Empty,
            "[dim]├ CONNECTION[/]",
            Row("Host", Escape(NullEmpty(world.Host))),
            Row("Port", world.Port.ToString(CultureInfo.InvariantCulture)),
            Row("TLS", OnOff(world.UseTls)),
            Row("Invalid cert", world.AllowInvalidCertificates ? "allow" : "reject"),
            string.Empty,
            "[dim]├ RENDERING[/]",
            Row("Format", world.ContentFormat.ToString()),
            Row("Local echo", OnOff(world.LocalEcho)),
            Row("Emoji", EmojiSummary(world.Emoji)),
            Row("Accent", $"[{accentHex}]████[/] [dim]{accentHex}[/]"),
            string.Empty,
            $"[dim]├ CHARACTERS[/]   [dim]{world.Characters.Count}[/]",
        };

        if (world.Characters.Count == 0)
        {
            lines.Add("  [dim]no characters[/]");
        }
        else
        {
            foreach (var character in world.Characters)
            {
                lines.Add(Character(character, accentHex));
            }
        }

        lines.Add(string.Empty);
        lines.Add("[dim]↑↓ move · enter edit · esc close[/]");
        return lines;
    }

    private static string Character(CharacterDefinition character, string accentHex)
    {
        var dot = character.AutoLogin ? $"[{accentHex}]●[/]" : "[dim]○[/]";
        var name = Escape(character.Name).PadRight(LabelWidth - 2);
        var login = character.AutoLogin ? "auto-login" : "manual";
        var sets = character.TriggerSets.Count > 0
            ? $" [dim]· sets: {Escape(string.Join(", ", character.TriggerSets))}[/]"
            : string.Empty;
        return $"  {dot} {name} [dim]{login}[/]{sets}";
    }

    private static string EmojiSummary(EmojiSettings emoji)
    {
        var detail = $"emoticons {OnOff(emoji.Emoticons)} · shortcodes {OnOff(emoji.Shortcodes)}";
        return $"{OnOff(emoji.Enabled)}  [dim]({detail})[/]";
    }

    private static string Row(string label, string value) =>
        $"  [dim]{Escape(label).PadRight(LabelWidth)}[/]{value}";

    private static string OnOff(bool value) => value ? "on" : "off";

    private static string NullEmpty(string? value) => string.IsNullOrEmpty(value) ? "—" : value;

    private static string Hex(TerminalColor color) =>
        color.Kind == TerminalColorKind.Rgb ? $"#{color.R:x2}{color.G:x2}{color.B:x2}" : Accent;

    private static string Escape(string text) => text.Replace("[", "[[").Replace("]", "]]");
}
