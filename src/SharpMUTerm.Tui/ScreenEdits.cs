namespace SharpMUTerm.Tui;

/// <summary>
/// The undo log behind a settings screen's Cancel/Save bar. Screens edit the live configuration in
/// place — cloning <see cref="SharpMUTerm.Core.Configuration.AppConfiguration"/> would silently drop
/// the fields that deliberately don't round-trip (a character's in-memory password is
/// <c>[JsonIgnore]</c>) — so "discard" is a replayed undo rather than a swapped object. Each applied
/// toggle pushes the restore action captured *before* it ran; Esc replays them newest-first, ⏎ drops
/// them. Pure state, so the semantics are unit-testable without a window.
/// </summary>
internal sealed class ScreenEdits
{
    private readonly List<Action> _undo = new();

    /// <summary>Whether anything has been changed since the screen opened or last saved.</summary>
    internal bool IsDirty => _undo.Count > 0;

    /// <summary>How many changes are pending — the footer's dirty count.</summary>
    internal int Count => _undo.Count;

    /// <summary>Flips a checkbox, recording how to put it back.</summary>
    internal void Apply(ScreenToggle toggle)
    {
        _undo.Add(toggle.Snapshot());
        toggle.Flip();
    }

    /// <summary>Undoes every pending change, newest first, so overlapping edits unwind correctly.</summary>
    internal void Revert()
    {
        for (var i = _undo.Count - 1; i >= 0; i--)
        {
            _undo[i]();
        }

        _undo.Clear();
    }

    /// <summary>Accepts every pending change; the log is dropped and there is nothing left to undo.</summary>
    internal void Commit() => _undo.Clear();
}
