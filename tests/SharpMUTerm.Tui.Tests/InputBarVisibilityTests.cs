using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// "Toggleable PER Window!" — the second command line belongs to the window you toggled it on, so a
/// window that never asked for it does not get one, and a window that did keeps it when you come back.
/// </summary>
public class InputBarVisibilityTests
{
    [Test]
    public async Task ToggleAffectsOnlyTheWindowItWasToggledOn()
    {
        var bars = new InputBarVisibility(() => false);

        await Assert.That(bars.Toggle("main")).IsTrue();

        await Assert.That(bars.IsShown("main")).IsTrue();
        await Assert.That(bars.IsShown("spawn:Chat")).IsFalse();
    }

    [Test]
    public async Task TogglingTwice_PutsTheWindowBack()
    {
        var bars = new InputBarVisibility(() => false);
        bars.Toggle("main");

        await Assert.That(bars.Toggle("main")).IsFalse();
        await Assert.That(bars.IsShown("main")).IsFalse();
    }

    /// <summary>
    /// F8's box is the answer for a window that has none of its own, read live — ticking it changes
    /// what the next window does without a restart.
    /// </summary>
    [Test]
    public async Task AWindowWithNoAnswerOfItsOwn_TakesTheConfiguredDefault()
    {
        var input = new InputSettings();
        var bars = new InputBarVisibility(() => input.SecondBar);

        await Assert.That(bars.IsShown("main")).IsFalse();

        input.SecondBar = true;
        await Assert.That(bars.IsShown("main")).IsTrue();
    }

    /// <summary>
    /// A window that was told once keeps what it was told, even when the default flips underneath it —
    /// otherwise turning the preference on would reopen a bar somebody had closed.
    /// </summary>
    [Test]
    public async Task AWindowThatWasToldOnce_IgnoresALaterChangeOfDefault()
    {
        var input = new InputSettings();
        var bars = new InputBarVisibility(() => input.SecondBar);
        bars.Toggle("main"); // explicitly on

        input.SecondBar = false;
        await Assert.That(bars.IsShown("main")).IsTrue();

        bars.Toggle("main"); // explicitly off
        input.SecondBar = true;
        await Assert.That(bars.IsShown("main")).IsFalse();
    }

    [Test]
    public async Task AClosedWindow_IsForgottenSoASameIdWindowStartsFromTheDefault()
    {
        var bars = new InputBarVisibility(() => false);
        bars.Toggle("spawn:Chat");

        bars.Forget("spawn:Chat");

        await Assert.That(bars.IsShown("spawn:Chat")).IsFalse();
    }
}
