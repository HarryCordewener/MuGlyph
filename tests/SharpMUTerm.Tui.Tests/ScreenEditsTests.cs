using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

public class ScreenEditsTests
{
    /// <summary>A stand-in for the config a checkbox writes to.</summary>
    private sealed class Cell
    {
        public bool Value { get; set; }

        public ScreenToggle Toggle => ScreenToggle.Bind(() => Value, v => Value = v);
    }

    [Test]
    public async Task Apply_FlipsTheValueAndMarksTheScreenDirty()
    {
        var cell = new Cell();
        var edits = new ScreenEdits();

        edits.Apply(cell.Toggle);

        await Assert.That(cell.Value).IsTrue();
        await Assert.That(edits.IsDirty).IsTrue();
        await Assert.That(edits.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Revert_PutsEveryChangeBackAndClearsTheLog()
    {
        var a = new Cell { Value = true };
        var b = new Cell();
        var edits = new ScreenEdits();

        edits.Apply(a.Toggle);
        edits.Apply(b.Toggle);
        edits.Revert();

        await Assert.That(a.Value).IsTrue();
        await Assert.That(b.Value).IsFalse();
        await Assert.That(edits.IsDirty).IsFalse();
    }

    [Test]
    public async Task Revert_UnwindsRepeatedEditsToTheSameRow()
    {
        var cell = new Cell { Value = true };
        var edits = new ScreenEdits();

        edits.Apply(cell.Toggle);
        edits.Apply(cell.Toggle);
        edits.Apply(cell.Toggle);
        await Assert.That(cell.Value).IsFalse();

        edits.Revert();

        await Assert.That(cell.Value).IsTrue();
    }

    [Test]
    public async Task Commit_KeepsTheChangesAndLeavesNothingToUndo()
    {
        var cell = new Cell();
        var edits = new ScreenEdits();

        edits.Apply(cell.Toggle);
        edits.Commit();
        edits.Revert();

        await Assert.That(cell.Value).IsTrue();
        await Assert.That(edits.IsDirty).IsFalse();
    }

    [Test]
    public async Task Revert_RestoresWhateverTheSnapshotCaptured_NotJustABoolean()
    {
        // The F9 "auto-start" pattern: the checkbox is a projection of a richer value, so its snapshot
        // has to restore that value rather than the boolean it was displayed as.
        var format = "Html";
        var toggle = new ScreenToggle(
            () => format != "None",
            () => format = format == "None" ? "Plain" : "None",
            () =>
            {
                var previous = format;
                return () => format = previous;
            });

        var edits = new ScreenEdits();
        edits.Apply(toggle);
        await Assert.That(format).IsEqualTo("None");

        edits.Revert();

        await Assert.That(format).IsEqualTo("Html");
    }
}
