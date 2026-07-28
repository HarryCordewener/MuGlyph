using System.Text;

namespace MuClient.Core.Text;

/// <summary>
/// A single logical line of terminal output: an ordered list of <see cref="StyledSpan"/>s
/// plus a lazily-computed plain-text projection used by triggers, search, and logging.
/// </summary>
public sealed class StyledLine
{
    private readonly StyledSpan[] _spans;
    private string? _text;

    public StyledLine(IEnumerable<StyledSpan> spans, TerminalColor? ruleColor = null)
    {
        ArgumentNullException.ThrowIfNull(spans);
        _spans = spans.Where(s => s.Length > 0).ToArray();
        RuleColor = ruleColor;
    }

    /// <summary>
    /// When set, the line was marked by a highlight trigger: the renderer draws a left rule in this
    /// colour (and may tint the row) so matched lines stand out, per the design's output view.
    /// </summary>
    public TerminalColor? RuleColor { get; }

    /// <summary>Returns a copy of this line carrying the given trigger-highlight rule colour.</summary>
    public StyledLine WithRule(TerminalColor color) => new(_spans, color);

    /// <summary>An empty line (a blank row of output).</summary>
    public static StyledLine Empty { get; } = new(Array.Empty<StyledSpan>());

    public IReadOnlyList<StyledSpan> Spans => _spans;

    /// <summary>The concatenated plain text of every span, with all styling removed.</summary>
    public string Text
    {
        get
        {
            if (_text is not null)
            {
                return _text;
            }

            if (_spans.Length == 0)
            {
                return _text = string.Empty;
            }

            if (_spans.Length == 1)
            {
                return _text = _spans[0].Text;
            }

            var sb = new StringBuilder();
            foreach (var span in _spans)
            {
                sb.Append(span.Text);
            }

            return _text = sb.ToString();
        }
    }

    public int Length => Text.Length;

    public bool IsEmpty => _spans.Length == 0;

    /// <summary>Builds a line from a single unstyled string of plain text.</summary>
    public static StyledLine FromText(string text, TextStyle style) =>
        string.IsNullOrEmpty(text) ? Empty : new StyledLine(new[] { new StyledSpan(text, style) });

    public override string ToString() => Text;
}
