namespace SharpMUTerm.Tui;

/// <summary>
/// The unsent input each window is holding — what switching tabs puts back in the command line.
/// <para>
/// A class of its own rather than a dictionary in the app, because F8's <c>keep per-tab drafts</c>
/// lives here: with the preference off nothing is stashed <em>and</em> nothing already stashed is
/// handed back, so unticking the box empties the store on the next keystroke rather than leaving a
/// draft that reappears once it is ticked again. The rule is one predicate, read per call — the
/// preference object is the live one the settings screen edits in place — and it is testable without
/// a terminal, which the app's own key handling is not.
/// </para>
/// </summary>
internal sealed class DraftStore(Func<bool> enabled)
{
    private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);
    private readonly Func<bool> _enabled = enabled;

    /// <summary>Records (or clears) the draft for a window. Blank text clears; disabled never keeps.</summary>
    public void Record(string windowId, string? text)
    {
        ArgumentNullException.ThrowIfNull(windowId);

        if (string.IsNullOrEmpty(text) || !_enabled())
        {
            _drafts.Remove(windowId);
            return;
        }

        _drafts[windowId] = text;
    }

    /// <summary>The draft to put back when a window becomes visible — empty when there is none.</summary>
    public string Recall(string windowId)
    {
        ArgumentNullException.ThrowIfNull(windowId);
        return _enabled() ? _drafts.GetValueOrDefault(windowId, string.Empty) : string.Empty;
    }

    /// <summary>Drops a window's draft — the command was sent, or the window was closed.</summary>
    public void Clear(string windowId)
    {
        ArgumentNullException.ThrowIfNull(windowId);
        _drafts.Remove(windowId);
    }
}
