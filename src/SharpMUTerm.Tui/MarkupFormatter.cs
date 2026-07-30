using System.Text;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Text;
using SharpMUTerm.Core.Theming;
using static SharpMUTerm.Tui.MarkupText;

namespace SharpMUTerm.Tui;

/// <summary>
/// Converts SharpMUTerm's UI-agnostic <see cref="StyledLine"/> model into SharpConsoleUI (Spectre-style)
/// markup: truecolor foreground/background, bold/italic/underline/etc., and clickable
/// <see cref="SpanInteraction"/>s rendered as <c>[link=…]</c> spans. Colours are resolved through the
/// active <see cref="Theme"/> so palette-indexed and default colours land on real RGB values.
/// <para>
/// <paramref name="text"/> is the app's live <see cref="TextSettings"/> — the two F7 options that are
/// decisions about *markup* rather than about the model live here (<c>allow blink</c>,
/// <c>underline hyperlinks</c>). It is held by reference and read per span, because the F7 screen
/// edits that object in place: a copy would need a restart to mean anything. Null means the defaults,
/// which is what the unit tests want.
/// </para>
/// </summary>
internal sealed class MarkupFormatter(Theme theme, TextSettings? text = null)
{
    private readonly Theme _theme = theme;
    private readonly TextSettings _text = text ?? new TextSettings();

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

        // Every clickable span's payload is scheme-tagged by kind, in LinkPayload, which owns both ends
        // of that round trip — see its remarks for why no kind may be passed through bare.
        var link = LinkPayload.For(span.Interaction);
        if (link is not null)
        {
            sb.Append("[link=").Append(link).Append(']');
        }

        // "underline hyperlinks": a clickable span reads as clickable even when the server sent it
        // unstyled. Added to the span's own attributes rather than emitted separately, so a link that
        // was already underlined doesn't get two tokens.
        var style = span.Style;
        if (link is not null && _text.UnderlineHyperlinks)
        {
            style = style.AddAttribute(TextAttributes.Underline);
        }

        var styleTag = StyleTag(style);
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

        // Blink is parsed out of SGR 5/6 but dropped unless F7's "allow blink" is on: a blinking line
        // is the one rendition a server can impose that the reader cannot stop looking at.
        if (_text.AllowBlink && style.HasAttribute(TextAttributes.Blink))
        {
            tokens.Add("blink");
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

    private static string Hex(Rgb rgb) => $"#{rgb.R:x2}{rgb.G:x2}{rgb.B:x2}";
}
