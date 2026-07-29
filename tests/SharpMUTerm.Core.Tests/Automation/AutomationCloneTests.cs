using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Tests.Automation;

/// <summary>
/// <see cref="Trigger.Clone"/> and <see cref="Alias.Clone"/> — the deep copy the F2 and F3 screens'
/// <c>duplicate</c> buttons are built on, and the same contract
/// <see cref="Configuration.DefinitionCloneTests"/> pins for worlds and characters: a copy that shared
/// anything mutable with its original would look correct on screen and only betray itself later, when
/// an edit to one silently landed on both.
/// <para>
/// The compiled <see cref="Trigger.Regex"/> is the sharp edge here. It is cached, so a copy that
/// inherited it would go on matching its <em>source's</em> pattern after its own had been edited —
/// invisibly, until a line arrived.
/// </para>
/// </summary>
public class AutomationCloneTests
{
    private static Trigger Trigger() => new()
    {
        Name = "Tell",
        Pattern = "tells you",
        Enabled = false,
        CaseSensitive = true,
        StopProcessing = true,
        Actions = new TriggerActions
        {
            Gag = true,
            HighlightForeground = TerminalColor.FromRgb(0xff, 0xd7, 0x00),
            HighlightBackground = TerminalColor.FromIndex(4),
            AddAttributes = TextAttributes.Bold,
            Rewrite = "[$1]",
            SendResponse = "page $1=hi",
            SpawnTarget = "Chat",
            ScriptCallback = "onTell",
        },
    };

    private static Alias Alias() => new()
    {
        Name = "gr",
        Pattern = "^gr (.*)$",
        Enabled = false,
        CaseSensitive = true,
        Substitution = "greet $1\nwave $1",
        ScriptCallback = "onGreet",
    };

    [Test]
    public async Task TriggerClone_CopiesEveryValue()
    {
        var copy = Trigger().Clone();

        await Assert.That(copy.Name).IsEqualTo("Tell");
        await Assert.That(copy.Pattern).IsEqualTo("tells you");
        await Assert.That(copy.Enabled).IsFalse();
        await Assert.That(copy.CaseSensitive).IsTrue();
        await Assert.That(copy.StopProcessing).IsTrue();
        await Assert.That(copy.Actions.Gag).IsTrue();
        await Assert.That(copy.Actions.HighlightForeground)
            .IsEqualTo(TerminalColor.FromRgb(0xff, 0xd7, 0x00));
        await Assert.That(copy.Actions.HighlightBackground).IsEqualTo(TerminalColor.FromIndex(4));
        await Assert.That(copy.Actions.AddAttributes).IsEqualTo(TextAttributes.Bold);
        await Assert.That(copy.Actions.Rewrite).IsEqualTo("[$1]");
        await Assert.That(copy.Actions.SendResponse).IsEqualTo("page $1=hi");
        await Assert.That(copy.Actions.SpawnTarget).IsEqualTo("Chat");
        await Assert.That(copy.Actions.ScriptCallback).IsEqualTo("onTell");
    }

    [Test]
    public async Task TriggerClone_DoesNotShareItsActionsWithTheOriginal()
    {
        var original = Trigger();
        var copy = original.Clone();

        await Assert.That(ReferenceEquals(copy.Actions, original.Actions)).IsFalse();

        copy.Actions.Gag = false;
        copy.Actions.SpawnTarget = "Pages";
        copy.Actions.HighlightForeground = null;

        await Assert.That(original.Actions.Gag).IsTrue();
        await Assert.That(original.Actions.SpawnTarget).IsEqualTo("Chat");
        await Assert.That(original.Actions.HighlightForeground).IsNotNull();
    }

    /// <summary>
    /// The cached matcher must not travel with the copy. Both sides are asserted, because a copy that
    /// silently kept its source's compiled regex would still pass every value comparison above.
    /// </summary>
    [Test]
    public async Task TriggerClone_CompilesItsOwnMatcher()
    {
        var original = new Trigger { Name = "Tell", Pattern = "tells you" };
        _ = original.Regex; // force the original to cache one before the copy is taken

        var copy = original.Clone();
        copy.Pattern = "pages you";

        await Assert.That(copy.Regex.IsMatch("she pages you")).IsTrue();
        await Assert.That(copy.Regex.IsMatch("she tells you")).IsFalse();
        await Assert.That(original.Regex.IsMatch("she tells you")).IsTrue();
    }

    [Test]
    public async Task AliasClone_CopiesEveryValue()
    {
        var copy = Alias().Clone();

        await Assert.That(copy.Name).IsEqualTo("gr");
        await Assert.That(copy.Pattern).IsEqualTo("^gr (.*)$");
        await Assert.That(copy.Enabled).IsFalse();
        await Assert.That(copy.CaseSensitive).IsTrue();
        await Assert.That(copy.Substitution).IsEqualTo("greet $1\nwave $1");
        await Assert.That(copy.ScriptCallback).IsEqualTo("onGreet");
    }

    [Test]
    public async Task AliasClone_CompilesItsOwnMatcher()
    {
        var original = new Alias { Name = "k", Pattern = "^k$" };
        _ = original.Regex;

        var copy = original.Clone();
        copy.Pattern = "^kk$";

        await Assert.That(copy.Regex.IsMatch("kk")).IsTrue();
        await Assert.That(copy.Regex.IsMatch("k")).IsFalse();
        await Assert.That(original.Regex.IsMatch("k")).IsTrue();
    }

    /// <summary>
    /// Renaming is what the F2/F3/F4/F6 screens' name fields do, and it must stay free of the matcher —
    /// unlike <see cref="Trigger.Pattern"/> and <see cref="Alias.CaseSensitive"/>, which drop the
    /// cached regex on write. This is the "check for cached derived state" question answered the other
    /// way: there is none, and a rename must not invalidate anything.
    /// </summary>
    [Test]
    public async Task RenamingLeavesTheCompiledMatcherAlone()
    {
        var trigger = new Trigger { Name = "Tell", Pattern = "tells you" };
        var compiled = trigger.Regex;
        trigger.Name = "Whisper";
        await Assert.That(ReferenceEquals(trigger.Regex, compiled)).IsTrue();

        var alias = new Alias { Name = "k", Pattern = "^k$" };
        var aliasCompiled = alias.Regex;
        alias.Name = "kill";
        await Assert.That(ReferenceEquals(alias.Regex, aliasCompiled)).IsTrue();

        // The other two carry no matcher at all; they are asserted here so the whole set of newly
        // writable names is covered in one place.
        var timer = new TimerDefinition { Name = "ping", IntervalSeconds = 30, Command = "look" };
        timer.Name = "pong";
        await Assert.That(timer.Name).IsEqualTo("pong");

        var macro = new Macro { Name = "look", Key = "Num5", Command = "look" };
        macro.Name = "survey";
        await Assert.That(macro.Name).IsEqualTo("survey");
        await Assert.That(macro.Key).IsEqualTo("Num5");
    }
}
