namespace SharpMUTerm.Core.Text;

/// <summary>
/// An incremental, line-oriented parser that turns decoded server text into styled lines.
/// Implemented by <see cref="AnsiParser"/> and the MXP/Pueblo markup parsers so a session can
/// select the content format uniformly.
/// </summary>
public interface ILineParser
{
    /// <summary>Feeds a chunk of text, returning every line completed within it.</summary>
    IReadOnlyList<StyledLine> Feed(string text);

    /// <summary>Returns and clears any buffered partial line (e.g. a prompt), or null.</summary>
    StyledLine? Flush();

    /// <summary>Resets all parser state, including the current style.</summary>
    void Reset();

    /// <summary>The rendition state that will apply to the next printed character.</summary>
    TextStyle CurrentStyle { get; }

    /// <summary>True when a partial line or markup sequence is buffered.</summary>
    bool HasPendingContent { get; }
}
