using System.Text;
using MuClient.Core.Text;

namespace MuClient.Web;

/// <summary>
/// Accumulates styled text into word-wrapped <see cref="StyledLine"/>s. Handles inter-element
/// whitespace collapsing, preformatted runs, hard breaks, and blank-line collapsing, coalescing
/// adjacent same-style segments into spans.
/// </summary>
internal sealed class LineWriter(int width)
{
    private readonly int _width = width;
    private readonly List<StyledLine> _lines = new();
    private readonly List<Segment> _current = new();
    private int _col;
    private bool _lineHasContent;
    private bool _pendingSpace;

    public void AddText(string text, TextStyle style, SpanInteraction? link, bool preformatted)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (preformatted)
        {
            AddPreformatted(text, style, link);
            return;
        }

        var leadingWs = char.IsWhiteSpace(text[0]);
        var trailingWs = char.IsWhiteSpace(text[^1]);
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0)
        {
            // All whitespace: remember an inter-word space if we already have content.
            if (_lineHasContent)
            {
                _pendingSpace = true;
            }

            return;
        }

        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            var spaceBefore = i == 0 ? (_pendingSpace || leadingWs) && _lineHasContent : _lineHasContent;
            var needed = (spaceBefore ? 1 : 0) + word.Length;

            if (_lineHasContent && _col + needed > _width)
            {
                LineBreak();
                spaceBefore = false;
            }

            var piece = spaceBefore ? " " + word : word;
            _current.Add(new Segment(piece, style, link));
            _col += piece.Length;
            _lineHasContent = true;
            _pendingSpace = false;
        }

        if (trailingWs)
        {
            _pendingSpace = true;
        }
    }

    private void AddPreformatted(string text, TextStyle style, SpanInteraction? link)
    {
        var parts = text.Split('\n');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                LineBreak();
            }

            var part = parts[i].TrimEnd('\r');
            if (part.Length > 0)
            {
                _current.Add(new Segment(part, style, link));
                _col += part.Length;
                _lineHasContent = true;
            }
        }
    }

    /// <summary>Ends the current line unconditionally (an explicit break, e.g. &lt;br&gt;).</summary>
    public void LineBreak()
    {
        _lines.Add(Coalesce());
        _current.Clear();
        _col = 0;
        _lineHasContent = false;
        _pendingSpace = false;
    }

    /// <summary>Ends the current line only if it has content (used around block elements).</summary>
    public void EndLine()
    {
        if (_lineHasContent || _current.Count > 0)
        {
            LineBreak();
        }
    }

    /// <summary>Ensures a single blank separator line (collapses consecutive blanks; skips a leading blank).</summary>
    public void BlankLine()
    {
        EndLine();
        if (_lines.Count > 0 && !_lines[^1].IsEmpty)
        {
            _lines.Add(StyledLine.Empty);
        }
    }

    public IReadOnlyList<StyledLine> Finish()
    {
        EndLine();
        while (_lines.Count > 0 && _lines[^1].IsEmpty)
        {
            _lines.RemoveAt(_lines.Count - 1);
        }

        return _lines;
    }

    private StyledLine Coalesce()
    {
        if (_current.Count == 0)
        {
            return StyledLine.Empty;
        }

        var spans = new List<StyledSpan>();
        var runText = new StringBuilder(_current[0].Text);
        var runStyle = _current[0].Style;
        var runLink = _current[0].Link;

        for (var i = 1; i < _current.Count; i++)
        {
            var seg = _current[i];
            if (seg.Style.Equals(runStyle) && Equals(seg.Link, runLink))
            {
                // StringBuilder avoids O(n^2) copying when a page has many same-style segments.
                runText.Append(seg.Text);
            }
            else
            {
                spans.Add(new StyledSpan(runText.ToString(), runStyle, runLink));
                runText.Clear();
                runText.Append(seg.Text);
                runStyle = seg.Style;
                runLink = seg.Link;
            }
        }

        spans.Add(new StyledSpan(runText.ToString(), runStyle, runLink));
        return new StyledLine(spans);
    }

    private readonly record struct Segment(string Text, TextStyle Style, SpanInteraction? Link);
}
