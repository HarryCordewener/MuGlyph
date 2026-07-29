namespace SharpMUTerm.Core.Input;

/// <summary>
/// Draft-safe command history recall for a single input buffer. Modern MU*/shell input lines let
/// you walk previously sent commands with <c>↑</c>/<c>↓</c>, but a naive implementation destroys a
/// half-typed line the moment you press <c>↑</c>. This model preserves it: the first <c>↑</c> stashes
/// the live draft, walking back through entries newest-first; <c>↓</c> walks forward and, stepping
/// past the newest entry, restores the stashed draft; and editing a recalled line re-bases it as the
/// new draft (ending recall). Pure and fully unit-testable — the UI only feeds keystrokes and renders
/// what <see cref="Recall"/>/<see cref="Forward"/> return.
/// <para>
/// It is memory only, for the life of the process. There is deliberately no persistence and no setting
/// that would add it: writing this list to disk is a separate decision about secrets at rest, and
/// <see cref="HistorySecrets"/> is a filter on what gets recorded, not a promise about a file.
/// </para>
/// </summary>
public sealed class InputHistory
{
    private readonly List<string> _entries = new();
    private readonly int _capacity;
    private readonly Func<string, bool>? _ignore;

    // The live draft parked at the first recall, restored when we step forward past the newest entry.
    private string? _stash;

    // -1 means "not recalling" (the input shows the live draft); otherwise an index into _entries.
    private int _index = -1;

    /// <param name="capacity">How many entries to keep; the oldest is dropped past this.</param>
    /// <param name="ignore">
    /// Lines that must never be recorded, asked per <see cref="Add"/> rather than captured once — so the
    /// setting behind it takes effect on the next command, not the next session (the same rule
    /// <c>LocalEcho</c> follows). The gate lives here rather than at the call site so that no future
    /// caller can add a line without passing it: this is the invariant, not a courtesy.
    /// <see cref="HistorySecrets.LooksLikeCredential"/> is what the client passes.
    /// </param>
    public InputHistory(int capacity = 500, Func<string, bool>? ignore = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        }

        _capacity = capacity;
        _ignore = ignore;
    }

    /// <summary>Sent commands, oldest first.</summary>
    public IReadOnlyList<string> Entries => _entries;

    /// <summary>True while the input is showing a recalled entry rather than the live draft.</summary>
    public bool IsRecalling => _index >= 0;

    /// <summary>Records a sent command and ends any recall in progress. Consecutive duplicates and
    /// blank lines are ignored, matching common shell behaviour, as is anything the ignore rule this
    /// history was constructed with rejects (see <see cref="HistorySecrets"/>).</summary>
    public void Add(string command)
    {
        ResetCursor();

        if (string.IsNullOrEmpty(command))
        {
            return;
        }

        if (_ignore is not null && _ignore(command))
        {
            return;
        }

        if (_entries.Count > 0 && string.Equals(_entries[^1], command, StringComparison.Ordinal))
        {
            return;
        }

        _entries.Add(command);
        if (_entries.Count > _capacity)
        {
            _entries.RemoveAt(0);
        }
    }

    /// <summary>
    /// Handles <c>↑</c>: on the first press stashes <paramref name="currentDraft"/> and returns the
    /// newest entry; subsequent presses walk toward older entries. Returns the text to display, or
    /// <c>null</c> if there is no history to recall (so the caller leaves the draft untouched).
    /// </summary>
    public string? Recall(string currentDraft)
    {
        if (_entries.Count == 0)
        {
            return null;
        }

        if (_index < 0)
        {
            _stash = currentDraft ?? string.Empty;
            _index = _entries.Count - 1;
            return _entries[_index];
        }

        if (_index > 0)
        {
            _index--;
        }

        return _entries[_index];
    }

    /// <summary>
    /// Handles <c>↓</c>: walks toward newer entries and, stepping past the newest, ends recall and
    /// returns the stashed draft. Returns the text to display, or <c>null</c> when not recalling (so
    /// <c>↓</c> at the live draft does nothing).
    /// </summary>
    public string? Forward()
    {
        if (_index < 0)
        {
            return null;
        }

        if (_index < _entries.Count - 1)
        {
            _index++;
            return _entries[_index];
        }

        // Past the newest entry: restore the parked draft and leave recall.
        var draft = _stash ?? string.Empty;
        ResetCursor();
        return draft;
    }

    /// <summary>
    /// Recalls one entry by index — what the history surface's <c>⏎</c> does. It is <see cref="Recall"/>
    /// with the destination chosen rather than stepped to: the live draft is stashed on the first recall
    /// exactly the same way, so <c>↓</c> afterwards walks forward from the picked entry and eventually
    /// hands the draft back, and editing the inserted line re-bases it. Reusing this machinery is the
    /// point — a surface that set the command line by itself would be a second, quietly different answer
    /// to "where did my half-typed line go".
    /// </summary>
    /// <returns>
    /// The entry to display, or <c>null</c> when <paramref name="index"/> is not one (so the caller
    /// leaves the draft untouched, and no stash is taken).
    /// </returns>
    public string? RecallAt(int index, string currentDraft)
    {
        if (index < 0 || index >= _entries.Count)
        {
            return null;
        }

        // Only the *first* recall parks the draft: arriving here mid-recall (↑ then the surface) must not
        // overwrite the parked line with the recalled one the bar is currently showing.
        if (_index < 0)
        {
            _stash = currentDraft ?? string.Empty;
        }

        _index = index;
        return _entries[_index];
    }

    /// <summary>
    /// Re-bases a recalled line as the live draft: the user edited a recalled entry, so it is no
    /// longer "history" and further <c>↑</c> should stash it afresh. No-op when not recalling.
    /// </summary>
    public void Rebase() => ResetCursor();

    /// <summary>Resets the recall cursor (e.g. on a tab or pane switch) without touching history.</summary>
    public void ResetCursor()
    {
        _index = -1;
        _stash = null;
    }
}
