using MuClient.Core.Input;

namespace MuClient.Core.Tests.Input;

public class InputHistoryTests
{
    [Test]
    public async Task Recall_OnEmptyHistory_ReturnsNull()
    {
        var history = new InputHistory();
        await Assert.That(history.Recall("draft")).IsNull();
        await Assert.That(history.IsRecalling).IsFalse();
    }

    [Test]
    public async Task Up_WalksEntriesNewestFirst()
    {
        var history = new InputHistory();
        history.Add("look");
        history.Add("north");
        history.Add("say hi");

        await Assert.That(history.Recall("")).IsEqualTo("say hi");
        await Assert.That(history.Recall("")).IsEqualTo("north");
        await Assert.That(history.Recall("")).IsEqualTo("look");
        // Past the oldest entry, it stays put.
        await Assert.That(history.Recall("")).IsEqualTo("look");
    }

    [Test]
    public async Task FirstUp_StashesDraft_AndDownPastNewest_RestoresIt()
    {
        var history = new InputHistory();
        history.Add("look");
        history.Add("north");

        await Assert.That(history.Recall("half-typed")).IsEqualTo("north");
        await Assert.That(history.IsRecalling).IsTrue();
        // ↓ past the newest entry brings the parked draft back and ends recall.
        await Assert.That(history.Forward()).IsEqualTo("half-typed");
        await Assert.That(history.IsRecalling).IsFalse();
    }

    [Test]
    public async Task Down_WalksForwardTowardNewer()
    {
        var history = new InputHistory();
        history.Add("a");
        history.Add("b");
        history.Add("c");

        history.Recall("draft"); // c
        history.Recall("draft"); // b
        history.Recall("draft"); // a
        await Assert.That(history.Forward()).IsEqualTo("b");
        await Assert.That(history.Forward()).IsEqualTo("c");
        await Assert.That(history.Forward()).IsEqualTo("draft");
    }

    [Test]
    public async Task Down_WhenNotRecalling_ReturnsNull()
    {
        var history = new InputHistory();
        history.Add("x");
        await Assert.That(history.Forward()).IsNull();
    }

    [Test]
    public async Task Rebase_EndsRecall_AndNextUpStashesTheEditedLine()
    {
        var history = new InputHistory();
        history.Add("north");

        history.Recall("orig");        // shows "north"
        history.Rebase();               // user edited it into a new draft
        await Assert.That(history.IsRecalling).IsFalse();

        // The edited line is now the live draft; ↑ stashes it and ↓ past newest brings it back.
        history.Recall("north-edited");
        await Assert.That(history.Forward()).IsEqualTo("north-edited");
    }

    [Test]
    public async Task Add_IgnoresConsecutiveDuplicatesAndBlanks()
    {
        var history = new InputHistory();
        history.Add("look");
        history.Add("look");
        history.Add("");
        history.Add("   x   "); // non-blank, kept verbatim

        await Assert.That(history.Entries.Count).IsEqualTo(2);
        await Assert.That(history.Entries[0]).IsEqualTo("look");
        await Assert.That(history.Entries[1]).IsEqualTo("   x   ");
    }

    [Test]
    public async Task Add_EndsRecall()
    {
        var history = new InputHistory();
        history.Add("a");
        history.Recall("draft");
        await Assert.That(history.IsRecalling).IsTrue();

        history.Add("b");
        await Assert.That(history.IsRecalling).IsFalse();
    }

    [Test]
    public async Task Capacity_EvictsOldestEntries()
    {
        var history = new InputHistory(capacity: 2);
        history.Add("a");
        history.Add("b");
        history.Add("c");

        await Assert.That(history.Entries.Count).IsEqualTo(2);
        await Assert.That(history.Entries[0]).IsEqualTo("b");
        await Assert.That(history.Entries[1]).IsEqualTo("c");
    }

    [Test]
    public async Task ResetCursor_LeavesHistoryButEndsRecall()
    {
        var history = new InputHistory();
        history.Add("a");
        history.Recall("draft");

        history.ResetCursor();
        await Assert.That(history.IsRecalling).IsFalse();
        await Assert.That(history.Entries.Count).IsEqualTo(1);
    }
}
