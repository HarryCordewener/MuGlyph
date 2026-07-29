using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Web;

/// <summary>
/// Renders an HTML document into SharpMUTerm's <see cref="StyledLine"/> model (a text-mode browser,
/// w3m/lynx-style): block elements become line breaks, inline elements map to <see cref="TextStyle"/>,
/// and <c>&lt;a href&gt;</c>/<c>&lt;img&gt;</c> become clickable <see cref="SpanInteraction"/> spans.
/// Pure and UI-agnostic — the TUI places the result in a pane, and images can be realised later via
/// the graphics layer. Text is word-wrapped to a target width.
/// </summary>
public sealed class HtmlStyledRenderer
{
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "header", "footer", "main", "aside", "nav",
        "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li", "blockquote", "pre",
        "table", "tr", "figure", "figcaption", "form", "fieldset", "dl", "dt", "dd",
    };

    private static readonly HashSet<string> SkipTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "head", "noscript", "template", "svg", "iframe",
    };

    private static readonly TextStyle LinkStyle =
        TextStyle.Default.WithForeground(TerminalColor.FromIndex(12)).AddAttribute(TextAttributes.Underline);

    private static readonly TextStyle HeadingStyle =
        TextStyle.Default.WithForeground(TerminalColor.FromIndex(11)).AddAttribute(TextAttributes.Bold);

    private readonly string? _baseUrl;
    private readonly List<WebImage> _images = new();
    private int _width = 80;

    public HtmlStyledRenderer(string? baseUrl = null) => _baseUrl = baseUrl;

    /// <summary>Renders the document to styled lines, discarding the image index.</summary>
    public IReadOnlyList<StyledLine> Render(string html, int width = 80) => RenderDocument(html, width).Lines;

    /// <summary>
    /// Renders the document to styled lines <em>and</em> the list of <c>&lt;img&gt;</c> elements it
    /// contains, each pointing at the line holding its placeholder. A graphics-capable view uses the
    /// index to swap placeholders for real pictures; everything else just renders the lines.
    /// </summary>
    public HtmlRenderResult RenderDocument(string html, int width = 80)
    {
        ArgumentNullException.ThrowIfNull(html);
        _width = Math.Max(20, width);
        _images.Clear();
        var document = new HtmlParser().ParseDocument(html);
        var writer = new LineWriter(_width);
        INode? root = document.Body ?? document.DocumentElement;
        if (root is not null)
        {
            foreach (var child in root.ChildNodes)
            {
                Walk(child, writer, TextStyle.Default, null, preformatted: false);
            }
        }

        return new HtmlRenderResult(writer.Finish(), _images.ToArray());
    }

    /// <summary>Extracts the document title, if any.</summary>
    public static string? GetTitle(string html)
    {
        var document = new HtmlParser().ParseDocument(html ?? string.Empty);
        var title = document.Title;
        return string.IsNullOrWhiteSpace(title) ? null : title.Trim();
    }

    private void Walk(INode node, LineWriter writer, TextStyle style, SpanInteraction? link, bool preformatted)
    {
        switch (node)
        {
            case IText text:
                writer.AddText(text.Data, style, link, preformatted);
                break;
            case IElement element:
                WalkElement(element, writer, style, link, preformatted);
                break;
        }
    }

    private void WalkElement(IElement element, LineWriter writer, TextStyle style, SpanInteraction? link, bool preformatted)
    {
        var tag = element.LocalName;
        if (SkipTags.Contains(tag))
        {
            return;
        }

        switch (tag)
        {
            case "br":
                writer.LineBreak();
                return;
            case "hr":
                writer.BlankLine();
                writer.AddText(new string('─', _width), style, null, preformatted: true);
                writer.LineBreak();
                writer.BlankLine();
                return;
            case "img":
                EmitImage(element, writer, style, preformatted);
                return;
        }

        var isBlock = BlockTags.Contains(tag);
        if (isBlock)
        {
            writer.EndLine();
        }

        var childStyle = style;
        var childLink = link;
        var childPre = preformatted;

        switch (tag)
        {
            case "b" or "strong":
                childStyle = style.AddAttribute(TextAttributes.Bold);
                break;
            case "i" or "em" or "cite":
                childStyle = style.AddAttribute(TextAttributes.Italic);
                break;
            case "u" or "ins":
                childStyle = style.AddAttribute(TextAttributes.Underline);
                break;
            case "s" or "strike" or "del":
                childStyle = style.AddAttribute(TextAttributes.Strikethrough);
                break;
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                writer.BlankLine();
                childStyle = HeadingStyle;
                break;
            case "pre":
                childPre = true;
                break;
            case "li":
                writer.AddText("• ", style, null, preformatted: false);
                break;
            case "a":
                var href = element.GetAttribute("href");
                if (!string.IsNullOrWhiteSpace(href))
                {
                    childLink = SpanInteraction.Link(Resolve(href), href);
                    childStyle = MergeLinkStyle(style);
                }

                break;
        }

        if (TryGetColor(element, out var color))
        {
            childStyle = childStyle.WithForeground(color);
        }

        foreach (var child in element.ChildNodes)
        {
            Walk(child, writer, childStyle, childLink, childPre);
        }

        if (isBlock)
        {
            writer.EndLine();
        }

        if (tag is "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "blockquote" or "ul" or "ol" or "pre")
        {
            writer.BlankLine();
        }
    }

    /// <summary>
    /// Writes an image's text placeholder and records where it landed. The placeholder gets its own
    /// lines: a graphics-capable view replaces those whole rows with the picture, which it can only
    /// do if no surrounding prose shares them. A long <c>alt</c> wraps like any other text, so the
    /// index records how many lines it took as well as where it started. Images with no <c>src</c>
    /// still get a placeholder — there is just nothing to fetch, so they are not indexed.
    /// </summary>
    private void EmitImage(IElement element, LineWriter writer, TextStyle style, bool preformatted)
    {
        var alt = element.GetAttribute("alt");
        var label = string.IsNullOrWhiteSpace(alt) ? "[image]" : $"[image: {alt}]";
        var src = element.GetAttribute("src");
        var hasSource = !string.IsNullOrWhiteSpace(src);
        var resolved = hasSource ? Resolve(src!) : null;

        writer.EndLine();
        var lineIndex = writer.LineIndex;
        var imgLink = resolved is null ? null : SpanInteraction.Link(resolved);
        writer.AddText(label, style.AddAttribute(TextAttributes.Italic), imgLink, preformatted);
        writer.EndLine();

        if (resolved is not null)
        {
            _images.Add(new WebImage(
                lineIndex,
                resolved,
                alt?.Trim() is { Length: > 0 } a ? a : null,
                label,
                Math.Max(1, writer.LineIndex - lineIndex)));
        }
    }

    private static TextStyle MergeLinkStyle(TextStyle style) =>
        style.WithForeground(LinkStyle.Foreground).AddAttribute(TextAttributes.Underline);

    private static bool TryGetColor(IElement element, out TerminalColor color)
    {
        color = TerminalColor.Default;
        var value = element.GetAttribute("color");
        return value is not null && WebColors.TryParse(value, out color);
    }

    private string Resolve(string url)
    {
        if (string.IsNullOrEmpty(_baseUrl) ||
            Uri.TryCreate(url, UriKind.Absolute, out _) ||
            !Uri.TryCreate(_baseUrl, UriKind.Absolute, out var baseUri))
        {
            return url;
        }

        return Uri.TryCreate(baseUri, url, out var resolved) ? resolved.ToString() : url;
    }
}
