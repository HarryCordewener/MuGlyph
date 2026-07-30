namespace SharpMUTerm.Tui;

/// <summary>
/// The one path from a settings screen into the configuration, and the point at which each change is
/// written to disk. Screens edit the live
/// <see cref="SharpMUTerm.Core.Configuration.AppConfiguration"/> in place — the live object is what the
/// running client reads, so an edit applied to a copy would be an edit the app does not have — making
/// this a gateway rather than a transaction.
/// <para>
/// <b>The scope rule, which is what makes the three answers to Esc obviously right.</b> Esc discards
/// work that has not been confirmed, keeps work that has, and <em>asks</em> about work that destroyed
/// something:
/// </para>
/// <list type="number">
/// <item>
/// An <b>uncommitted field buffer</b> never reaches this class at all. What is being typed lives in
/// <see cref="SettingsSession"/> until ⏎ accepts it; Esc drops the buffer, and config never saw it.
/// </item>
/// <item>
/// A <b>committed</b> field, a flipped checkbox and an added row are <b>kept unconditionally</b>. They
/// are applied the moment they are made and persisted immediately, so leaving the screen is navigation
/// and not a transaction boundary. This is the fix for "I changed the address and it went back": Esc
/// used to replay an undo log over everything, so the key the header called <c>close</c> silently threw
/// away every edit on the screen.
/// </item>
/// <item>
/// A <b>deletion</b> is recorded here, because it is the one change whose subject is gone and cannot be
/// retyped. Closing the screen with deletions pending asks once, on the way out, naming each of them
/// (<see cref="ScreenEditReview"/>); <see cref="Revert"/> is what "no, put them back" runs. It is a
/// batch review rather than a confirmation per press, so deleting three characters interrogates you
/// once instead of three times.
/// </item>
/// </list>
/// <para>
/// Every structural deletion is logged, not only the cascading ones (a world and its characters, a
/// trigger set and its rules): the mechanism is identical, and a trigger set holding fifteen rules is
/// not meaningfully less destructive than a character. To narrow it to a subset, stop the button
/// handing back a <see cref="ScreenPress.Undo"/> — that is the single switch, and a press with no undo
/// is simply kept.
/// </para>
/// <para>
/// Pure state apart from the persist callback, so the semantics are unit-testable without a window.
/// </para>
/// </summary>
internal sealed class ScreenEdits
{
    private readonly List<(string What, Action Undo)> _deletions = new();
    private readonly Action? _persist;

    /// <summary>
    /// <paramref name="persist"/> writes the configuration out, and is run after <em>every</em> accepted
    /// change rather than on the way out of the screen.
    /// <para>
    /// That choice is the other half of the "it does not stick" fix. Under apply-immediately semantics a
    /// committed value is already real — the live config object is what the client reads — so the only
    /// question is whether the file agrees, and deferring the write to some later gesture is how "it
    /// stuck until I restarted" happens. Writing per change also means a crash cannot eat one.
    /// <c>config.json</c> is a few kilobytes and a keystroke that commits is rare (⏎, Space, a button),
    /// so the cost is nothing; the alternative — write on close — buys only the ability to have unsaved
    /// state, which is precisely what the user asked us to stop having.
    /// </para>
    /// <para>
    /// A deletion is persisted like anything else, and <see cref="Revert"/> persists the restoration. The
    /// review is a review, not a staging area.
    /// </para>
    /// </summary>
    internal ScreenEdits(Action? persist = null) => _persist = persist;

    /// <summary>Whether any deletion is waiting to be reviewed on the way out of the screen.</summary>
    internal bool HasDeletions => _deletions.Count > 0;

    /// <summary>
    /// What has been deleted, oldest first, in the words the review names them by — <c>Aetherfall and
    /// its 2 characters</c>, not <c>1 deletion</c>. Same discipline as the quit prompt naming the worlds
    /// it would disconnect: a question that says what it is about is answerable, and "are you sure?" is
    /// a ritual.
    /// </summary>
    internal IReadOnlyList<string> Deletions => _deletions.ConvertAll(d => d.What);

    /// <summary>Flips a checkbox and writes it out. Committed on the spot; nothing to review.</summary>
    internal void Apply(ScreenToggle toggle)
    {
        toggle.Flip();
        _persist?.Invoke();
    }

    /// <summary>
    /// Writes a typed value to a field and persists it. Nothing is written when the value doesn't
    /// validate: returns null when the value was accepted, otherwise why it was refused. This is the
    /// only path from a buffer into config, which is what keeps an invalid value out of it — and what
    /// makes "Esc abandons the buffer" a true statement rather than a hopeful one.
    /// </summary>
    internal string? Apply(ScreenField field, string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (field.Validate(value) is { } error)
        {
            return error;
        }

        field.Set(value);
        _persist?.Invoke();
        return null;
    }

    /// <summary>
    /// Runs a button and persists the result. A press that hands back an undo destroyed something, so it
    /// is recorded for the closing review, described by what it took with it; a press that hands back
    /// none built something and is simply kept. Returns where the button wants the cursor left, or null
    /// when it doesn't care.
    /// <para>
    /// The description is taken <em>before</em> the press, because it is the only moment the answer can
    /// still be counted — a world's characters are gone by the time <see cref="ScreenButton.Run"/>
    /// returns. The undo, conversely, can only be known afterwards: see <see cref="ScreenButton"/>.
    /// Deletions replay newest-first, because an index captured by a removal is only meaningful against
    /// the list as it stood at that moment.
    /// </para>
    /// </summary>
    internal int? Apply(ScreenButton button)
    {
        var what = button.Describe?.Invoke() ?? button.Target;
        var press = button.Run();
        if (press.Undo is { } undo)
        {
            _deletions.Add((what ?? button.Label, undo));
        }

        _persist?.Invoke();
        return press.Select;
    }

    /// <summary>
    /// Puts every logged deletion back, newest first, and writes the result out. The answer to "no, keep
    /// them" is <see cref="Commit"/>; this is the answer to "no, put them back".
    /// </summary>
    internal void Revert()
    {
        for (var i = _deletions.Count - 1; i >= 0; i--)
        {
            _deletions[i].Undo();
        }

        _deletions.Clear();
        _persist?.Invoke();
    }

    /// <summary>Accepts the logged deletions; there is nothing left to review.</summary>
    internal void Commit() => _deletions.Clear();
}
