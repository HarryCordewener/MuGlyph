namespace SharpMUTerm.Tui;

/// <summary>
/// Which of a window's two command lines a draft belongs to. The second bar exists so an IC line and
/// an OOC line can be held at once, which only works if each keeps its own unsent text — one draft per
/// window would make the second bar a second view of the first.
/// </summary>
internal enum InputBar
{
    /// <summary>The command line every window has.</summary>
    Primary,

    /// <summary>The optional second command line, shown per window.</summary>
    Secondary,
}

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
/// <para>
/// Drafts are keyed by window <em>and</em> bar. The two-argument members are the primary bar, which is
/// what a window with no second bar has and all this class ever held.
/// </para>
/// </summary>
internal sealed class DraftStore(Func<bool> enabled)
{
    private readonly Dictionary<(string Window, InputBar Bar), string> _drafts = new();
    private readonly Func<bool> _enabled = enabled;

    /// <summary>Records (or clears) the draft for a window. Blank text clears; disabled never keeps.</summary>
    public void Record(string windowId, string? text) => Record(windowId, InputBar.Primary, text);

    /// <summary>Records (or clears) the draft for one bar of a window.</summary>
    public void Record(string windowId, InputBar bar, string? text)
    {
        ArgumentNullException.ThrowIfNull(windowId);

        if (string.IsNullOrEmpty(text) || !_enabled())
        {
            _drafts.Remove((windowId, bar));
            return;
        }

        _drafts[(windowId, bar)] = text;
    }

    /// <summary>The draft to put back when a window becomes visible — empty when there is none.</summary>
    public string Recall(string windowId) => Recall(windowId, InputBar.Primary);

    /// <summary>The draft one bar of a window is holding — empty when there is none.</summary>
    public string Recall(string windowId, InputBar bar)
    {
        ArgumentNullException.ThrowIfNull(windowId);
        return _enabled() ? _drafts.GetValueOrDefault((windowId, bar), string.Empty) : string.Empty;
    }

    /// <summary>Drops a window's draft — the command was sent, or the window was closed.</summary>
    public void Clear(string windowId) => Clear(windowId, InputBar.Primary);

    /// <summary>Drops one bar's draft for a window.</summary>
    public void Clear(string windowId, InputBar bar)
    {
        ArgumentNullException.ThrowIfNull(windowId);
        _drafts.Remove((windowId, bar));
    }

    /// <summary>Drops everything a closed window was holding, on either bar.</summary>
    public void Forget(string windowId)
    {
        ArgumentNullException.ThrowIfNull(windowId);
        foreach (var bar in Enum.GetValues<InputBar>())
        {
            _drafts.Remove((windowId, bar));
        }
    }
}
