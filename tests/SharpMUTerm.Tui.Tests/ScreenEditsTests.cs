using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The gateway every settings screen writes config through, and the scope rule it enforces: a committed
/// edit is kept, a deletion is logged for the closing review, and every accepted change is persisted at
/// once.
/// <para>
/// These tests used to say the opposite — that a flipped checkbox went into an undo log and
/// <c>Revert</c> put it back. That was the bug: Esc and the F-key both ran that revert while the header
/// called them <c>close</c>, so leaving the screen threw away everything typed on it. The log is now
/// deletions only, which is why the toggle and field cases here assert that reverting does <em>not</em>
/// touch them.
/// </para>
/// </summary>
public class ScreenEditsTests
{
    /// <summary>A stand-in for the config a checkbox writes to.</summary>
    private sealed class Cell
    {
        public bool Value { get; set; }

        public ScreenToggle Toggle => ScreenToggle.Bind(() => Value, v => Value = v);
    }

    /// <summary>A removal that reports what it destroyed, the way every screen's remove button does.</summary>
    private static ScreenButton Removal(List<string> list, int index, string describe) =>
        ScreenButton.Remove(list, index, target: list[index], describe: () => describe);

    [Test]
    public async Task Apply_FlipsTheValueAndPersistsIt()
    {
        var cell = new Cell();
        var saves = 0;
        var edits = new ScreenEdits(() => saves++);

        edits.Apply(cell.Toggle);

        await Assert.That(cell.Value).IsTrue();
        await Assert.That(saves).IsEqualTo(1);
    }

    /// <summary>
    /// A checkbox is <em>not</em> reviewable. It was committed the moment Space pressed it, so there is
    /// nothing for the closing prompt to ask about and nothing for a revert to put back — which is the
    /// whole of the fix for "I changed it and it went back to the old setting".
    /// </summary>
    [Test]
    public async Task ACommittedToggleIsKeptAndIsNotAmongTheDeletions()
    {
        var cell = new Cell();
        var edits = new ScreenEdits();

        edits.Apply(cell.Toggle);

        await Assert.That(edits.HasDeletions).IsFalse();

        edits.Revert();

        await Assert.That(cell.Value).IsTrue();
    }

    /// <summary>The same for a typed value, which is the case the bug was actually reported against.</summary>
    [Test]
    public async Task ACommittedFieldIsKeptAndIsNotAmongTheDeletions()
    {
        var host = "aetherfall.mux";
        var saves = 0;
        var edits = new ScreenEdits(() => saves++);
        var field = ScreenField.Text("host", () => host, v => host = v);

        await Assert.That(edits.Apply(field, "elsewhere.example")).IsNull();
        await Assert.That(host).IsEqualTo("elsewhere.example");
        await Assert.That(saves).IsEqualTo(1);
        await Assert.That(edits.HasDeletions).IsFalse();

        edits.Revert();

        await Assert.That(host).IsEqualTo("elsewhere.example");
    }

    /// <summary>A refused value writes nothing, persists nothing, and says why.</summary>
    [Test]
    public async Task ARefusedFieldChangesNothingAtAll()
    {
        var host = "aetherfall.mux";
        var saves = 0;
        var edits = new ScreenEdits(() => saves++);
        var field = ScreenField.Text("host", () => host, v => host = v);

        await Assert.That(edits.Apply(field, "   ")).IsNotNull();
        await Assert.That(host).IsEqualTo("aetherfall.mux");
        await Assert.That(saves).IsEqualTo(0);
    }

    /// <summary>
    /// An addition is kept unconditionally too: it destroyed nothing, so it hands back no undo and is
    /// never reviewed. Deleting the row is how you change your mind about one.
    /// </summary>
    [Test]
    public async Task AnAdditionIsNotReviewable()
    {
        var list = new List<string> { "Aetherfall" };
        var edits = new ScreenEdits();

        edits.Apply(ScreenButton.Add("+ world", list, () => "Grapevine"));

        await Assert.That(edits.HasDeletions).IsFalse();

        edits.Revert();

        await Assert.That(list).IsEquivalentTo(new[] { "Aetherfall", "Grapevine" });
    }

    /// <summary>
    /// A deletion is the one change that is logged, described in the words the review will name it by —
    /// because its subject is gone and cannot be retyped the way a host can.
    /// </summary>
    [Test]
    public async Task ADeletionIsLoggedWithWhatItDestroyed()
    {
        var list = new List<string> { "Aetherfall", "Grapevine" };
        var saves = 0;
        var edits = new ScreenEdits(() => saves++);

        edits.Apply(Removal(list, 0, "world Aetherfall and its 2 characters"));

        await Assert.That(list).IsEquivalentTo(new[] { "Grapevine" });
        await Assert.That(saves).IsEqualTo(1);
        await Assert.That(edits.HasDeletions).IsTrue();
        await Assert.That(edits.Deletions).IsEquivalentTo(new[] { "world Aetherfall and its 2 characters" });
    }

    /// <summary>
    /// Revert puts them back where they were — index included, because the list's order is what the
    /// screen navigates by — and writes the restored configuration out.
    /// </summary>
    [Test]
    public async Task Revert_PutsEveryDeletionBackAtItsIndexAndPersists()
    {
        var list = new List<string> { "Aetherfall", "Grapevine", "Rookery" };
        var saves = 0;
        var edits = new ScreenEdits(() => saves++);

        edits.Apply(Removal(list, 1, "world Grapevine"));
        edits.Apply(Removal(list, 0, "world Aetherfall"));
        await Assert.That(list).IsEquivalentTo(new[] { "Rookery" });

        edits.Revert();

        await Assert.That(list).IsEquivalentTo(new[] { "Aetherfall", "Grapevine", "Rookery" });
        await Assert.That(edits.HasDeletions).IsFalse();
        await Assert.That(saves).IsEqualTo(3); // two deletions, then the restoration
    }

    /// <summary>
    /// Newest first, which is what makes overlapping deletions unwind correctly: an index captured by a
    /// removal is only meaningful against the list as it stood at that moment.
    /// </summary>
    [Test]
    public async Task Revert_UnwindsDeletionsNewestFirst()
    {
        var list = new List<string> { "a", "b", "c" };
        var edits = new ScreenEdits();

        edits.Apply(Removal(list, 0, "a"));
        edits.Apply(Removal(list, 0, "b"));

        edits.Revert();

        await Assert.That(list).IsEquivalentTo(new[] { "a", "b", "c" });
    }

    /// <summary>Commit is the other answer to the review: keep them, and there is nothing left to ask.</summary>
    [Test]
    public async Task Commit_AcceptsTheDeletions()
    {
        var list = new List<string> { "Aetherfall" };
        var edits = new ScreenEdits();

        edits.Apply(Removal(list, 0, "world Aetherfall"));
        edits.Commit();
        edits.Revert();

        await Assert.That(list).IsEmpty();
        await Assert.That(edits.HasDeletions).IsFalse();
    }
}
