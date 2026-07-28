using SharpMUTerm.Core.Automation;

namespace SharpMUTerm.Core.Tests.Automation;

public class AliasEngineTests
{
    [Test]
    public async Task Expand_SubstitutesCaptureGroups()
    {
        var engine = new AliasEngine();
        engine.Add(new Alias { Pattern = @"^gt (.+)", Substitution = "say to group: $1" });
        var result = engine.Expand("gt hello team");
        await Assert.That(result.Matched).IsTrue();
        await Assert.That(result.Commands).HasSingleItem();
        await Assert.That(result.Commands[0]).IsEqualTo("say to group: hello team");
    }

    [Test]
    public async Task Expand_ProducesMultipleCommands()
    {
        var engine = new AliasEngine();
        engine.Add(new Alias { Pattern = "^prep$", Substitution = "wield sword\nwear armor\nquaff potion" });
        var result = engine.Expand("prep");
        await Assert.That(result.Commands).Count().IsEqualTo(3);
        await Assert.That(result.Commands[1]).IsEqualTo("wear armor");
    }

    [Test]
    public async Task NoMatch_ReturnsNoMatch()
    {
        var engine = new AliasEngine();
        engine.Add(new Alias { Pattern = "^xyz$", Substitution = "nope" });
        var result = engine.Expand("look");
        await Assert.That(result.Matched).IsFalse();
    }

    [Test]
    public async Task FirstMatchWins()
    {
        var engine = new AliasEngine();
        engine.Add(new Alias { Pattern = "^a", Substitution = "first" });
        engine.Add(new Alias { Pattern = "^a", Substitution = "second" });
        var result = engine.Expand("abc");
        await Assert.That(result.Commands[0]).IsEqualTo("first");
    }

    [Test]
    public async Task DisabledAlias_IsSkipped()
    {
        var engine = new AliasEngine();
        engine.Add(new Alias { Pattern = "^a", Enabled = false, Substitution = "x" });
        var result = engine.Expand("abc");
        await Assert.That(result.Matched).IsFalse();
    }

    /// <summary>
    /// Pattern and substitution are settable so the F3 settings screen can edit a live alias. The
    /// compiled regex is cached, so writing the pattern has to drop that cache — the same trap
    /// <see cref="Alias.CaseSensitive"/> already guards against.
    /// </summary>
    [Test]
    public async Task RewritingThePatternAndExpansion_TakesEffectImmediately()
    {
        var alias = new Alias { Pattern = "^k$", Substitution = "kill" };
        var engine = new AliasEngine();
        engine.Add(alias);
        await Assert.That(engine.Expand("k").Matched).IsTrue();

        alias.Pattern = "^kk$";
        alias.Substitution = "kill target";

        await Assert.That(engine.Expand("k").Matched).IsFalse();
        var result = engine.Expand("kk");
        await Assert.That(result.Matched).IsTrue();
        await Assert.That(result.Commands[0]).IsEqualTo("kill target");
    }
}

public class MacroEngineTests
{
    [Test]
    public async Task Resolve_ReturnsBoundMacro()
    {
        var engine = new MacroEngine();
        engine.Add(new Macro { Key = "Ctrl+F1", Command = "north" });
        var macro = engine.Resolve("Ctrl+F1");
        await Assert.That(macro).IsNotNull();
        await Assert.That(macro!.Command).IsEqualTo("north");
    }

    [Test]
    public async Task Resolve_IsCaseInsensitiveOnKey()
    {
        var engine = new MacroEngine();
        engine.Add(new Macro { Key = "Alt+K", Command = "kick" });
        await Assert.That(engine.Resolve("alt+k")).IsNotNull();
    }

    [Test]
    public async Task Resolve_ReturnsNull_WhenDisabled()
    {
        var engine = new MacroEngine();
        engine.Add(new Macro { Key = "F2", Command = "flee", Enabled = false });
        await Assert.That(engine.Resolve("F2")).IsNull();
    }

    [Test]
    [Arguments("F1", false, false, false, "F1")]
    [Arguments("F1", true, false, false, "Ctrl+F1")]
    [Arguments("k", true, true, true, "Ctrl+Alt+Shift+k")]
    [Arguments("Enter", false, true, false, "Alt+Enter")]
    public async Task Describe_ProducesCanonicalDescriptor(string key, bool ctrl, bool alt, bool shift, string expected)
    {
        await Assert.That(MacroKey.Describe(key, ctrl, alt, shift)).IsEqualTo(expected);
    }
}
