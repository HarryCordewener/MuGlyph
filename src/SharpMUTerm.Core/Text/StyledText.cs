namespace SharpMUTerm.Core.Text;

/// <summary>Helpers for transforming <see cref="StyledLine"/>s at character granularity.</summary>
public static class StyledText
{
    /// <summary>
    /// Applies <paramref name="transform"/> to the styles of the characters in the range
    /// [<paramref name="start"/>, <paramref name="start"/> + <paramref name="length"/>), returning
    /// a new line with spans re-coalesced. Ranges outside the text are clamped.
    /// </summary>
    public static StyledLine Restyle(StyledLine line, int start, int length, Func<TextStyle, TextStyle> transform)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(transform);

        if (line.IsEmpty || length <= 0)
        {
            return line;
        }

        var text = line.Text;
        var rangeStart = Math.Clamp(start, 0, text.Length);
        var rangeEnd = Math.Clamp(start + length, 0, text.Length);
        if (rangeStart >= rangeEnd)
        {
            return line;
        }

        // Expand to per-character styles.
        var styles = new TextStyle[text.Length];
        var offset = 0;
        foreach (var span in line.Spans)
        {
            for (var i = 0; i < span.Text.Length; i++)
            {
                styles[offset++] = span.Style;
            }
        }

        for (var i = rangeStart; i < rangeEnd; i++)
        {
            styles[i] = transform(styles[i]);
        }

        return Coalesce(text, styles);
    }

    /// <summary>
    /// Returns the line with every span's foreground and background reset to
    /// <see cref="TerminalColor.Default"/> — the "strip incoming ANSI colour" preference
    /// (<see cref="SharpMUTerm.Core.Configuration.TextSettings.StripIncomingColour"/>).
    /// <para>
    /// Only the two colours go. Attributes stay, because bold/underline/reverse are how a server marks
    /// structure once its palette is gone, and an interaction stays because a stripped MXP link is
    /// still a link. Spans are re-coalesced, so a line that was only ever colour-differentiated
    /// collapses back to one span. A line with no colour on it is returned unchanged rather than
    /// rebuilt.
    /// </para>
    /// </summary>
    public static StyledLine StripColour(StyledLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var coloured = false;
        foreach (var span in line.Spans)
        {
            if (span.Style.Foreground.Kind != TerminalColorKind.Default ||
                span.Style.Background.Kind != TerminalColorKind.Default)
            {
                coloured = true;
                break;
            }
        }

        if (!coloured)
        {
            return line;
        }

        var spans = new List<StyledSpan>(line.Spans.Count);
        foreach (var span in line.Spans)
        {
            var style = span.Style
                .WithForeground(TerminalColor.Default)
                .WithBackground(TerminalColor.Default);

            // Merge with the previous span when stripping made them identical, so the result has as
            // few spans as the text really needs (the renderer emits one markup tag per span).
            if (spans.Count > 0 &&
                spans[^1].Style == style &&
                Equals(spans[^1].Interaction, span.Interaction))
            {
                spans[^1] = new StyledSpan(spans[^1].Text + span.Text, style, span.Interaction);
                continue;
            }

            spans.Add(new StyledSpan(span.Text, style, span.Interaction));
        }

        return new StyledLine(spans, line.RuleColor);
    }

    /// <summary>
    /// Replaces every tab in <paramref name="line"/> with <paramref name="width"/> spaces, keeping each
    /// run's style and interaction.
    /// <para>
    /// A tab arrives from the server and travels the whole pipeline as one character, so everything that
    /// measures a line — the wrap, the pane's width, <c>MarkupWidth</c> — counts it as <b>one cell</b>
    /// while the terminal paints it as a jump to the next tab stop. The two disagree by up to seven
    /// columns on every tab, which is the same class of defect as chrome that grows on wire data: the
    /// layout is computed against a width the screen does not use.
    /// </para>
    /// <para>
    /// This is <em>not</em> tab-stop expansion. A real tab advances to the next multiple of the stop,
    /// so its width depends on the column it starts in; this substitutes a fixed run of spaces, which is
    /// what was asked for and what MU* output — where a tab is a crude column separator rather than a
    /// layout instruction — actually needs. A line of <c>a\tb</c> and a line of <c>aaaa\tb</c> therefore
    /// do not align, and that is the accepted cost of not tracking a column.
    /// </para>
    /// <para>
    /// Applied per line rather than at parse time, beside <see cref="StripColour"/>, so changing the
    /// setting takes effect on the next line instead of on the next restart — and so the parser stays
    /// ignorant of user preferences. Lines already in scrollback keep the width they were expanded at.
    /// </para>
    /// </summary>
    public static StyledLine ExpandTabs(StyledLine line, int width)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (width < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "A tab cannot be a negative number of spaces.");
        }

        var hasTab = false;
        foreach (var span in line.Spans)
        {
            if (span.Text.Contains('\t', StringComparison.Ordinal))
            {
                hasTab = true;
                break;
            }
        }

        // The overwhelmingly common case: no tab, so the line is returned as it stands rather than
        // rebuilt. Every line of output passes through here.
        if (!hasTab)
        {
            return line;
        }

        var replacement = new string(' ', width);
        var spans = new List<StyledSpan>(line.Spans.Count);
        foreach (var span in line.Spans)
        {
            spans.Add(new StyledSpan(
                span.Text.Replace("\t", replacement, StringComparison.Ordinal),
                span.Style,
                span.Interaction));
        }

        return new StyledLine(spans, line.RuleColor);
    }

    /// <summary>Rebuilds a line from a plain string and a parallel per-character style array.</summary>
    public static StyledLine Coalesce(string text, TextStyle[] styles)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(styles);
        if (text.Length == 0)
        {
            return StyledLine.Empty;
        }

        if (styles.Length != text.Length)
        {
            throw new ArgumentException("Style array length must match text length.", nameof(styles));
        }

        var spans = new List<StyledSpan>();
        var runStart = 0;
        for (var i = 1; i <= text.Length; i++)
        {
            if (i == text.Length || styles[i] != styles[runStart])
            {
                spans.Add(new StyledSpan(text[runStart..i], styles[runStart]));
                runStart = i;
            }
        }

        return new StyledLine(spans);
    }
}
