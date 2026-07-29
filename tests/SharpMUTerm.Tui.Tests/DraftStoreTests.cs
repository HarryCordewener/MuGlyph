using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// F8's <c>keep per-tab drafts</c>, asserted as behaviour: with it on a switched-away tab hands its
/// typing back, with it off it does not. The preference is the live <see cref="InputSettings"/> the
/// screen edits in place, so every case flips it on a store that already holds a draft.
/// </summary>
public class DraftStoreTests
{
    [Test]
    public async Task On_KeepsWhatWasTypedInEachWindow()
    {
        var input = new InputSettings { KeepDrafts = true };
        var drafts = new DraftStore(() => input.KeepDrafts);

        drafts.Record("main", "say hello");
        drafts.Record("chat", "page anvil=hi");

        await Assert.That(drafts.Recall("main")).IsEqualTo("say hello");
        await Assert.That(drafts.Recall("chat")).IsEqualTo("page anvil=hi");
    }

    [Test]
    public async Task Off_KeepsNothing()
    {
        var input = new InputSettings { KeepDrafts = false };
        var drafts = new DraftStore(() => input.KeepDrafts);

        drafts.Record("main", "say hello");

        await Assert.That(drafts.Recall("main")).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// Unticking the box drops what was already stashed rather than hiding it: a draft that
    /// reappeared the moment the box was ticked again would be the setting failing on exactly the
    /// keystroke that tests it.
    /// </summary>
    [Test]
    public async Task TurningItOff_DropsADraftThatWasAlreadyHeld()
    {
        var input = new InputSettings { KeepDrafts = true };
        var drafts = new DraftStore(() => input.KeepDrafts);
        drafts.Record("main", "say hello");

        input.KeepDrafts = false;
        var whileOff = drafts.Recall("main");
        input.KeepDrafts = true;

        await Assert.That(whileOff).IsEqualTo(string.Empty);
        await Assert.That(drafts.Recall("main")).IsEqualTo("say hello");
    }

    /// <summary>
    /// The next keystroke on a window with the box off clears its stored draft for good, which is
    /// what makes the store empty rather than merely silent.
    /// </summary>
    [Test]
    public async Task TypingWhileItIsOff_ClearsTheStoredDraft()
    {
        var input = new InputSettings { KeepDrafts = true };
        var drafts = new DraftStore(() => input.KeepDrafts);
        drafts.Record("main", "say hello");

        input.KeepDrafts = false;
        drafts.Record("main", "say hello there");
        input.KeepDrafts = true;

        await Assert.That(drafts.Recall("main")).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task BlankInput_ClearsTheDraft()
    {
        var drafts = new DraftStore(() => true);
        drafts.Record("main", "half a thought");

        drafts.Record("main", string.Empty);

        await Assert.That(drafts.Recall("main")).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Clear_DropsOneWindowWithoutTouchingTheOthers()
    {
        var drafts = new DraftStore(() => true);
        drafts.Record("main", "one");
        drafts.Record("chat", "two");

        drafts.Clear("main");

        await Assert.That(drafts.Recall("main")).IsEqualTo(string.Empty);
        await Assert.That(drafts.Recall("chat")).IsEqualTo("two");
    }

    [Test]
    public async Task AnUnknownWindow_RecallsNothing()
    {
        var drafts = new DraftStore(() => true);

        await Assert.That(drafts.Recall("never-typed-in")).IsEqualTo(string.Empty);
    }
}
