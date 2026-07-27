using MuClient.Core.Text;
using MuClient.Core.Theming;
using TgAttribute = Terminal.Gui.Drawing.Attribute;
using TgColor = Terminal.Gui.Drawing.Color;

namespace MuClient.Tui;

/// <summary>Maps MuGlyph's UI-agnostic <see cref="TextStyle"/> to Terminal.Gui attributes via a <see cref="Theme"/>.</summary>
internal sealed class ColorMapper(Theme theme)
{
    private readonly Theme _theme = theme;

    public Theme Theme => _theme;

    public TgAttribute ToAttribute(TextStyle style)
    {
        var reverse = style.HasAttribute(TextAttributes.Reverse);
        var fg = _theme.Resolve(style.Foreground, isBackground: false);
        var bg = _theme.Resolve(style.Background, isBackground: true);

        // Bold on a base palette colour brightens it, matching common terminal behaviour.
        if (style.HasAttribute(TextAttributes.Bold) &&
            style.Foreground.Kind == TerminalColorKind.Indexed &&
            style.Foreground.Index < 8)
        {
            fg = _theme.ResolveIndex(style.Foreground.Index + 8);
        }

        if (reverse)
        {
            (fg, bg) = (bg, fg);
        }

        return new TgAttribute(ToColor(fg), ToColor(bg));
    }

    public static TgColor ToColor(Rgb rgb) => new(rgb.R, rgb.G, rgb.B, 255);
}
