using System.Text;
using SharpMUTerm.Core.Text;
using SharpMUTerm.Core.Theming;

namespace SharpMUTerm.Tui;

/// <summary>
/// Converts SharpMUTerm's UI-agnostic <see cref="StyledLine"/> model into SharpConsoleUI (Spectre-style)
/// markup: truecolor foreground/background, bold/italic/underline/etc., and clickable
/// <see cref="SpanInteraction"/>s rendered as <c>[link=…]</c> spans. Colours are resolved through the
/// active <see cref="Theme"/> so palette-indexed and default colours land on real RGB values.
/// </summary>
internal sealed class MarkupFormatter(Theme theme)
{
    // Custom link schemes so LinkClicked can tell an MXP/Pueblo command from a web hyperlink.
    public const string SendScheme = "mux:send:";
    public const string PromptScheme = "mux:prompt:";

    private readonly Theme _theme = theme;

    /// <summary>Renders a whole line to a single markup string.</summary>
    public string ToMarkup(StyledLine line) => ToMarkup(line, null);

    /// <summary>
    /// Renders a whole line, optionally prefixed with a dim timestamp gutter (the output view's optional
    /// timestamp column). The timestamp precedes the trigger left-rule and the styled spans.
    /// </summary>
    public string ToMarkup(StyledLine line, string? timestamp)
    {
        ArgumentNullException.ThrowIfNull(line);
        var sb = new StringBuilder();

        // Optional timestamp column: a dim gutter derived from the theme foreground, ahead of everything.
        if (!string.IsNullOrEmpty(timestamp))
        {
            sb.Append("[dim]").Append(Escape(timestamp)).Append("[/] ");
        }

        // A trigger-highlighted line gets a 2-col left rule in the trigger's colour (design output view).
        if (line.RuleColor is { } rule)
        {
            sb.Append('[').Append(Hex(_theme.Resolve(rule, isBackground: false))).Append("]▌[/] ");
        }

        foreach (var span in line.Spans)
        {
            AppendSpan(sb, span);
        }

        return sb.ToString();
    }

    private void AppendSpan(StringBuilder sb, StyledSpan span)
    {
        if (span.Text.Length == 0)
        {
            return;
        }

        var link = LinkFor(span.Interaction);
        if (link is not null)
        {
            sb.Append("[link=").Append(link).Append(']');
        }

        var styleTag = StyleTag(span.Style);
        if (styleTag is not null)
        {
            sb.Append(styleTag);
        }

        sb.Append(Escape(span.Text));

        if (styleTag is not null)
        {
            sb.Append("[/]");
        }

        if (link is not null)
        {
            sb.Append("[/]");
        }
    }

    /// <summary>Builds a markup open tag (e.g. <c>[bold #ffcc00 on #202020]</c>), or null if unstyled.</summary>
    private string? StyleTag(TextStyle style)
    {
        var reverse = style.HasAttribute(TextAttributes.Reverse);
        var fg = _theme.Resolve(style.Foreground, isBackground: false);
        var bg = _theme.Resolve(style.Background, isBackground: true);

        // Bold on a base (0–7) palette colour brightens it, matching common terminal behaviour.
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

        var tokens = new List<string>(6);
        if (style.HasAttribute(TextAttributes.Bold))
        {
            tokens.Add("bold");
        }

        if (style.HasAttribute(TextAttributes.Faint))
        {
            tokens.Add("dim");
        }

        if (style.HasAttribute(TextAttributes.Italic))
        {
            tokens.Add("italic");
        }

        if (style.HasAttribute(TextAttributes.Underline))
        {
            tokens.Add("underline");
        }

        if (style.HasAttribute(TextAttributes.Strikethrough))
        {
            tokens.Add("strikethrough");
        }

        tokens.Add(Hex(fg));

        // Only paint a background when one is actually set (or reverse swapped one in), so the
        // window background shows through normal text.
        if (reverse || style.Background.Kind != TerminalColorKind.Default)
        {
            tokens.Add("on " + Hex(bg));
        }

        // A foreground token is always present (the resolved fg above), so the tag is never empty.
        return $"[{string.Join(' ', tokens)}]";
    }

    private static string? LinkFor(SpanInteraction? interaction) => interaction?.Kind switch
    {
        InteractionKind.SendCommand when interaction.PromptOnly =>
            PromptScheme + Uri.EscapeDataString(interaction.Target),
        InteractionKind.SendCommand =>
            SendScheme + Uri.EscapeDataString(interaction.Target),
        // Remote MXP/Pueblo/HTML can put a ']' in a URL (legal in query/fragment); percent-encode the
        // markup metacharacters so a server can't close the [link=…] tag early and inject markup.
        InteractionKind.Hyperlink => interaction.Target.Replace("[", "%5B").Replace("]", "%5D"),
        _ => null,
    };

    private static string Hex(Rgb rgb) => $"#{rgb.R:x2}{rgb.G:x2}{rgb.B:x2}";

    /// <summary>Escapes markup metacharacters so literal text can't be parsed as tags.</summary>
    private static string Escape(string text) => text.Replace("[", "[[").Replace("]", "]]");
}
