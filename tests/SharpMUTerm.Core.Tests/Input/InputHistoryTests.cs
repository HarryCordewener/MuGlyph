using SharpMUTerm.Core.Input;

namespace SharpMUTerm.Core.Tests.Input;

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

    // ---- RecallAt: what the ⌃R surface's ⏎ does ---------------------------------------------------

    /// <summary>
    /// Picking an entry by index is <see cref="InputHistory.Recall"/> with the destination chosen: the same
    /// stash, so the draft it displaced is still there and <c>↓</c> still walks back to it. That reuse is
    /// the point — the surface must not be a second answer to "where did my half-typed line go".
    /// </summary>
    [Test]
    public async Task RecallAt_StashesTheDraftAndDownWalksBackToIt()
    {
        var history = new InputHistory();
        history.Add("look");
        history.Add("north");
        history.Add("say hi");

        await Assert.That(history.RecallAt(0, "half-typed")).IsEqualTo("look");
        await Assert.That(history.IsRecalling).IsTrue();

        // Forward from the *picked* entry, not from the newest: the surface put the cursor on "look".
        await Assert.That(history.Forward()).IsEqualTo("north");
        await Assert.That(history.Forward()).IsEqualTo("say hi");
        await Assert.That(history.Forward()).IsEqualTo("half-typed");
        await Assert.That(history.IsRecalling).IsFalse();
    }

    /// <summary>
    /// Arriving mid-recall must not re-stash: the bar is showing a recalled line, and parking that would
    /// lose the draft ↑ parked a moment ago.
    /// </summary>
    [Test]
    public async Task RecallAt_MidRecall_KeepsTheOriginalStash()
    {
        var history = new InputHistory();
        history.Add("look");
        history.Add("north");

        await Assert.That(history.Recall("half-typed")).IsEqualTo("north");
        await Assert.That(history.RecallAt(0, "north")).IsEqualTo("look");

        await Assert.That(history.Forward()).IsEqualTo("north");
        await Assert.That(history.Forward()).IsEqualTo("half-typed");
    }

    /// <summary>An index that is not an entry changes nothing at all — no stash, no recall.</summary>
    [Test]
    [Arguments(-1)]
    [Arguments(2)]
    public async Task RecallAt_OutOfRange_DoesNothing(int index)
    {
        var history = new InputHistory();
        history.Add("look");
        history.Add("north");

        await Assert.That(history.RecallAt(index, "half-typed")).IsNull();
        await Assert.That(history.IsRecalling).IsFalse();
        await Assert.That(history.Recall("half-typed")).IsEqualTo("north");
        await Assert.That(history.Forward()).IsEqualTo("half-typed");
    }

    [Test]
    public async Task RecallAt_OnEmptyHistory_ReturnsNull()
    {
        await Assert.That(new InputHistory().RecallAt(0, "draft")).IsNull();
    }

    /// <summary>Editing an inserted line re-bases it, exactly as editing a ↑-recalled one does.</summary>
    [Test]
    public async Task RecallAt_ThenRebase_EndsRecall()
    {
        var history = new InputHistory();
        history.Add("look");

        history.RecallAt(0, "half-typed");
        history.Rebase();

        await Assert.That(history.IsRecalling).IsFalse();
    }

    // ---- The ignore rule -------------------------------------------------------------------------

    /// <summary>
    /// A line the ignore rule rejects is never recorded, so no surface can show it and no <c>↑</c> can
    /// recall it. The gate lives in the store rather than at the call site precisely so this is an
    /// invariant of the type.
    /// </summary>
    [Test]
    public async Task Add_DropsWhatTheIgnoreRuleRejects()
    {
        var history = new InputHistory(ignore: HistorySecrets.LooksLikeCredential);

        history.Add("look");
        history.Add("connect Corvid hunter2");
        history.Add("north");

        await Assert.That(history.Entries).IsEquivalentTo(new[] { "look", "north" });
        await Assert.That(history.Recall(string.Empty)).IsEqualTo("north");
    }

    /// <summary>And the same verb without a password still goes in: the rule is not "no logins".</summary>
    [Test]
    public async Task Add_KeepsALoginLineThatCarriesNoPassword()
    {
        var history = new InputHistory(ignore: HistorySecrets.LooksLikeCredential);

        history.Add("connect guest");

        await Assert.That(history.Entries).IsEquivalentTo(new[] { "connect guest" });
    }

    /// <summary>
    /// The rule is asked per line, not captured once, so the F8 switch behind it takes effect on the next
    /// command rather than the next session — the same rule <c>LocalEcho</c> follows.
    /// </summary>
    [Test]
    public async Task Add_AsksTheIgnoreRuleEveryTime()
    {
        var excluding = true;
        var history = new InputHistory(ignore: c => excluding && HistorySecrets.LooksLikeCredential(c));

        history.Add("connect Corvid hunter2");
        await Assert.That(history.Entries).IsEmpty();

        excluding = false;
        history.Add("connect Corvid hunter2");
        await Assert.That(history.Entries.Count).IsEqualTo(1);
    }

    /// <summary>A dropped line still ends recall — it was sent, whatever history did with it.</summary>
    [Test]
    public async Task Add_OfAnIgnoredLine_StillEndsRecall()
    {
        var history = new InputHistory(ignore: HistorySecrets.LooksLikeCredential);
        history.Add("look");
        history.Recall("draft");

        history.Add("connect Corvid hunter2");

        await Assert.That(history.IsRecalling).IsFalse();
    }
}
