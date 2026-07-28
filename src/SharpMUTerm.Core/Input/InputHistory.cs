namespace SharpMUTerm.Core.Input;

/// <summary>
/// Draft-safe command history recall for a single input buffer. Modern MU*/shell input lines let
/// you walk previously sent commands with <c>↑</c>/<c>↓</c>, but a naive implementation destroys a
/// half-typed line the moment you press <c>↑</c>. This model preserves it: the first <c>↑</c> stashes
/// the live draft, walking back through entries newest-first; <c>↓</c> walks forward and, stepping
/// past the newest entry, restores the stashed draft; and editing a recalled line re-bases it as the
/// new draft (ending recall). Pure and fully unit-testable — the UI only feeds keystrokes and renders
/// what <see cref="Recall"/>/<see cref="Forward"/> return.
/// </summary>
public sealed class InputHistory
{
    private readonly List<string> _entries = new();
    private readonly int _capacity;

    // The live draft parked at the first recall, restored when we step forward past the newest entry.
    private string? _stash;

    // -1 means "not recalling" (the input shows the live draft); otherwise an index into _entries.
    private int _index = -1;

    public InputHistory(int capacity = 500)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        }

        _capacity = capacity;
    }

    /// <summary>Sent commands, oldest first.</summary>
    public IReadOnlyList<string> Entries => _entries;

    /// <summary>True while the input is showing a recalled entry rather than the live draft.</summary>
    public bool IsRecalling => _index >= 0;

    /// <summary>Records a sent command and ends any recall in progress. Consecutive duplicates and
    /// blank lines are ignored, matching common shell behaviour.</summary>
    public void Add(string command)
    {
        ResetCursor();

        if (string.IsNullOrEmpty(command))
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
