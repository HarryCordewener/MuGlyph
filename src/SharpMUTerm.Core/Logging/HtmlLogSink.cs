using System.Text;
using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Logging;

/// <summary>
/// Writes output as a self-contained HTML document, preserving colour and attributes via
/// inline-styled <c>&lt;span&gt;</c>s (BeipMU-style HTML logs). A preamble is emitted on
/// construction and the closing tags on <see cref="Dispose"/>.
/// </summary>
public sealed class HtmlLogSink : ILogSink
{
    private static readonly Rgb DefaultForeground = new(0xd0, 0xd0, 0xd0);
    private static readonly Rgb DefaultBackground = new(0x1e, 0x1e, 0x1e);

    private readonly TextWriter _writer;
    private readonly bool _ownsWriter;
    private readonly object _gate = new();
    private bool _closed;

    public HtmlLogSink(TextWriter writer, string title = "SharpMUTerm session log", bool ownsWriter = true)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _ownsWriter = ownsWriter;
        WritePreamble(title);
    }

    /// <summary>Opens an HTML log file, creating parent directories as needed.</summary>
    public static HtmlLogSink CreateFile(string path, string title = "SharpMUTerm session log")
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        return new HtmlLogSink(new StreamWriter(stream) { AutoFlush = false }, title);
    }

    private void WritePreamble(string title)
    {
        _writer.WriteLine("<!DOCTYPE html>");
        _writer.WriteLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        _writer.WriteLine($"<title>{Escape(title)}</title>");
        _writer.WriteLine("<style>");
        _writer.WriteLine($"body{{background:{DefaultBackground.ToHex()};color:{DefaultForeground.ToHex()};" +
                          "font-family:'Cascadia Code','Fira Code',Consolas,monospace;font-size:14px;line-height:1.3;}");
        _writer.WriteLine(".line{white-space:pre-wrap;}");
        _writer.WriteLine(".system{color:#6a9955;font-style:italic;}");
        _writer.WriteLine("</style></head><body>");
    }

    public void WriteLine(StyledLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            var sb = new StringBuilder("<div class=\"line\">");
            foreach (var span in line.Spans)
            {
                AppendSpan(sb, span);
            }

            sb.Append("</div>");
            _writer.WriteLine(sb.ToString());
        }
    }

    public void WriteSystem(string text)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _writer.WriteLine($"<div class=\"line system\">{Escape(text)}</div>");
        }
    }

    private static void AppendSpan(StringBuilder sb, StyledSpan span)
    {
        var style = span.Style;
        var reverse = style.HasAttribute(TextAttributes.Reverse);
        var fg = AnsiPalette.Resolve(style.Foreground, DefaultForeground);
        var bg = AnsiPalette.Resolve(style.Background, DefaultBackground);
        if (reverse)
        {
            (fg, bg) = (bg, fg);
        }

        var css = new StringBuilder();
        if (style.Foreground.Kind != TerminalColorKind.Default || reverse)
        {
            css.Append($"color:{fg.ToHex()};");
        }

        if (style.Background.Kind != TerminalColorKind.Default || reverse)
        {
            css.Append($"background:{bg.ToHex()};");
        }

        if (style.HasAttribute(TextAttributes.Bold))
        {
            css.Append("font-weight:bold;");
        }

        if (style.HasAttribute(TextAttributes.Faint))
        {
            css.Append("opacity:0.7;");
        }

        if (style.HasAttribute(TextAttributes.Italic))
        {
            css.Append("font-style:italic;");
        }

        var decorations = new List<string>();
        if (style.HasAttribute(TextAttributes.Underline))
        {
            decorations.Add("underline");
        }

        if (style.HasAttribute(TextAttributes.Strikethrough))
        {
            decorations.Add("line-through");
        }

        if (decorations.Count > 0)
        {
            css.Append($"text-decoration:{string.Join(' ', decorations)};");
        }

        if (css.Length == 0)
        {
            sb.Append(Escape(span.Text));
        }
        else
        {
            sb.Append($"<span style=\"{css}\">{Escape(span.Text)}</span>");
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (!_closed)
            {
                _writer.Flush();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _writer.WriteLine("</body></html>");
            _writer.Flush();
            _closed = true;
            if (_ownsWriter)
            {
                _writer.Dispose();
            }
        }
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
