using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Tui;

/// <summary>
/// The colour vocabulary the settings screens edit in: a short palette of named colours the choice
/// machinery can step through with ↑↓, plus the round trip between a <see cref="TerminalColor"/> and
/// the text a field shows for it.
/// <para>
/// The palette is deliberately small. <see cref="TerminalColor"/> spans the terminal default, 256
/// palette indices, and all 24 bits of RGB, and a picker for that is a screen of its own; a highlight
/// colour is picked once and then lived with, so the proportionate affordance is a handful of legible
/// choices one keypress apart. Nothing is *lost* by that: <see cref="Parse"/> also accepts
/// <c>#rrggbb</c> and <c>idx:N</c> typed in full, and <see cref="Format"/> renders a colour the
/// palette doesn't name in the same syntax — so a config holding an arbitrary colour opens showing
/// that colour, and committing it unchanged writes it back unchanged.
/// </para>
/// </summary>
internal static class ScreenColours
{
    /// <summary>The name for "no colour at all" — the null a highlight is unset with.</summary>
    internal const string None = "none";

    /// <summary>The name for the terminal's own default colour.</summary>
    internal const string DefaultName = "default";

    /// <summary>
    /// The named choices ↑↓ steps through, in the order they cycle: "no colour" first, then a spread
    /// wide enough to tell two highlights apart at a glance. Every name resolves through
    /// <see cref="WebColors"/>, so the picker and MXP/Pueblo markup agree on what "gold" means.
    /// </summary>
    internal static readonly IReadOnlyList<string> Palette = new[]
    {
        None, "red", "orange", "gold", "yellow", "lime", "green", "cyan", "teal",
        "blue", "magenta", "purple", "pink", "white", "silver", "grey", "black",
    };

    /// <summary>
    /// The text a field shows for a colour: the palette name when one matches exactly, else the
    /// literal syntax <see cref="Parse"/> reads back. Null — an unset highlight — is
    /// <see cref="None"/>.
    /// </summary>
    internal static string Format(TerminalColor? colour)
    {
        if (colour is not { } value)
        {
            return None;
        }

        foreach (var name in Palette)
        {
            if (name != None && WebColors.TryParse(name, out var named) && named == value)
            {
                return name;
            }
        }

        return value.Kind switch
        {
            TerminalColorKind.Rgb => $"#{value.R:x2}{value.G:x2}{value.B:x2}",
            TerminalColorKind.Indexed => $"idx:{value.Index}",
            _ => DefaultName,
        };
    }

    /// <summary>
    /// Reads a typed colour. Returns false when the text names nothing — the field's validator, so a
    /// misspelling is refused at commit rather than silently becoming "no highlight".
    /// <paramref name="colour"/> is null for <see cref="None"/>, which is a legal value.
    /// </summary>
    internal static bool TryParse(string text, out TerminalColor? colour)
    {
        ArgumentNullException.ThrowIfNull(text);

        colour = null;
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Equals(None, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.Equals(DefaultName, StringComparison.OrdinalIgnoreCase))
        {
            colour = TerminalColor.Default;
            return true;
        }

        if (trimmed.StartsWith("idx:", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(trimmed.AsSpan(4), out var index)
            && index is >= 0 and <= 255)
        {
            colour = TerminalColor.FromIndex(index);
            return true;
        }

        if (WebColors.TryParse(trimmed, out var parsed))
        {
            colour = parsed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The markup hex a swatch is painted with. Indexed colours resolve through the xterm palette
    /// rather than falling back to the accent, so a swatch shows the colour the terminal will
    /// actually use; only the terminal *default* has no hex of its own.
    /// </summary>
    internal static string Hex(TerminalColor colour, string fallback) => colour.Kind switch
    {
        TerminalColorKind.Rgb => $"#{colour.R:x2}{colour.G:x2}{colour.B:x2}",
        TerminalColorKind.Indexed => AnsiPalette.ToRgb(colour.Index).ToHex(),
        _ => fallback,
    };
}
