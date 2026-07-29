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

    /// <summary>
    /// Rebinding is live. The engine used to be a <c>Dictionary</c> keyed on the descriptor string it was
    /// handed at construction — a cache of the one property the F4 screen's capture mode can now change,
    /// so a rebound macro went on answering to the key it no longer carried until the next reconnect
    /// rebuilt the engine. Exactly the staleness <see cref="Alias.CaseSensitive"/> and
    /// <see cref="Trigger.Pattern"/> drop their compiled matcher to avoid.
    /// </summary>
    [Test]
    public async Task RebindingTheKey_TakesEffectImmediately()
    {
        var macro = new Macro { Key = "Ctrl+F1", Command = "north" };
        var engine = new MacroEngine(new[] { macro });
        await Assert.That(engine.Resolve("Ctrl+F1")).IsNotNull();

        macro.Key = "Ctrl+F10";

        await Assert.That(engine.Resolve("Ctrl+F1")).IsNull();
        await Assert.That(engine.Resolve("Ctrl+F10")).IsNotNull();
        await Assert.That(engine.Macros).HasSingleItem();
    }

    /// <summary>
    /// Two macros on one key is a state the F4 screen refuses to create, but a hand-edited file can still
    /// hold one. The first wins, the way <see cref="AliasEngine"/>'s first matching pattern does, so the
    /// answer is at least deterministic and the same one the screen names when it refuses the duplicate.
    /// </summary>
    [Test]
    public async Task TwoMacrosOnOneKey_ResolveToTheFirst()
    {
        var engine = new MacroEngine(new[]
        {
            new Macro { Name = "first", Key = "Ctrl+F1", Command = "north" },
            new Macro { Name = "second", Key = "ctrl+f1", Command = "south" },
        });

        await Assert.That(engine.Resolve("Ctrl+F1")!.Command).IsEqualTo("north");
    }

    /// <summary>Add replaces whatever holds the key, and Remove takes it out however it was spelt.</summary>
    [Test]
    public async Task AddReplacesTheBindingOnAKeyAndRemoveTakesItOut()
    {
        var engine = new MacroEngine();
        engine.Add(new Macro { Key = "Ctrl+F1", Command = "north" });
        engine.Add(new Macro { Key = "ctrl+f1", Command = "south" });

        await Assert.That(engine.Macros).HasSingleItem();
        await Assert.That(engine.Resolve("Ctrl+F1")!.Command).IsEqualTo("south");

        await Assert.That(engine.Remove("CTRL+F1")).IsTrue();
        await Assert.That(engine.Resolve("Ctrl+F1")).IsNull();
        await Assert.That(engine.Remove("Ctrl+F1")).IsFalse();
    }
}

/// <summary>
/// The descriptor vocabulary. It is Core's because it is what <see cref="MacroEngine"/> compares and what
/// a configuration file stores; what any of it is <em>worth</em> on a given keyboard is the UI layer's
/// question (see <c>MacroKeys</c> in the TUI), because it is a property of the host, not of the binding.
/// </summary>
public class MacroKeyTests
{
    /// <summary>
    /// A descriptor has one canonical spelling, so two ways of writing the same chord compare equal. The
    /// two shapes already in configurations — <c>Ctrl+F1</c> and <c>Num5</c> — come back untouched, which
    /// is the whole requirement: settling the spelling must not quietly rewrite anyone's bindings.
    /// </summary>
    [Test]
    [Arguments("Ctrl+F1", "Ctrl+F1")]
    [Arguments("Num5", "Num5")]
    [Arguments("F1", "F1")]
    [Arguments("shift+ctrl+f1", "Ctrl+Shift+F1")]
    [Arguments("CONTROL+f10", "Ctrl+F10")]
    [Arguments("NumPad0", "Num0")]
    [Arguments("alt+k", "Alt+K")]
    [Arguments("  Ctrl + F1 ", "Ctrl+F1")]
    [Arguments("pgup", "PageUp")]
    [Arguments("uparrow", "Up")]
    [Arguments("esc", "Escape")]
    public async Task Canonicalise_SettlesTheSpelling(string descriptor, string expected)
    {
        await Assert.That(MacroKey.Canonicalise(descriptor)).IsEqualTo(expected);
    }

    /// <summary>
    /// A descriptor whose modifiers name nothing, or which has no key at all, comes back null rather than
    /// half-understood: a caller that cannot say what a descriptor is must not pretend, because the answer
    /// decides whether a binding is drawn as one that fires.
    /// </summary>
    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("Hyper+F1")]
    [Arguments("Ctrl+")]
    [Arguments("Ctrl++F1")]
    public async Task Canonicalise_RefusesWhatItCannotRead(string descriptor)
    {
        await Assert.That(MacroKey.Canonicalise(descriptor)).IsNull();
    }

    /// <summary>
    /// A key this client has never heard of is kept verbatim rather than rejected or renamed: a
    /// configuration may name one, and silently rewriting it would be worse than leaving it alone.
    /// </summary>
    [Test]
    public async Task Canonicalise_LeavesAnUnknownKeyNameAlone()
    {
        await Assert.That(MacroKey.Canonicalise("Ctrl+MediaPlay")).IsEqualTo("Ctrl+MediaPlay");
    }

    [Test]
    public async Task TryParse_ReportsTheModifiersAndTheBaseKey()
    {
        await Assert.That(MacroKey.TryParse("ctrl+alt+shift+f5", out var parts)).IsTrue();
        await Assert.That(parts).IsEqualTo(new MacroKeyParts("F5", Ctrl: true, Alt: true, Shift: true));

        await Assert.That(MacroKey.TryParse("Num5", out var bare)).IsTrue();
        await Assert.That(bare).IsEqualTo(new MacroKeyParts("Num5", false, false, false));
    }
}
