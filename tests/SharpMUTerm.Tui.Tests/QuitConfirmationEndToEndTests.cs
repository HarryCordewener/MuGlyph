using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Graphics;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// ⌃Q, driven through the chord the app actually registers. <see cref="QuitPromptTests"/> pins what the
/// prompt means and says; these pin that the client is wired to it — that the shortcut no longer ends
/// the loop on its own, that a yes does, and that a no (however it is spelled) leaves the client running.
/// </summary>
/// <remarks>
/// Serialised for the same reason the other end-to-end suites are: constructing the app touches the
/// process-global console streams.
/// </remarks>
[NotInParallel]
public class QuitConfirmationEndToEndTests
{
    private const int Width = 120;
    private const int Height = 34;

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    private static SharpMUTermApp App()
    {
        Console.SetIn(TextReader.Null);
        return new SharpMUTermApp(DemoScene.Build(), Headless, new HeadlessConsoleDriver(Width, Height));
    }

    private static ConsoleKeyInfo CtrlQ() => new('\0', ConsoleKey.Q, false, false, true);

    private static ConsoleKeyInfo Key(char c, ConsoleKey key) => new(c, key, false, false, false);

    private static ConsoleKeyInfo Bare(ConsoleKey key) => new('\0', key, false, false, false);

    private static string Prompt(SharpMUTermApp app) => string.Join("\n", app.QuitPromptLines);

    /// <summary>The whole point: the chord that used to end the client now asks.</summary>
    [Test]
    public async Task CtrlQAsksInsteadOfQuitting()
    {
        var app = App();

        app.SimulateKey(CtrlQ());

        await Assert.That(app.QuitPromptOpen).IsTrue();
        await Assert.That(app.ExitRequested).IsFalse();
        await Assert.That(Prompt(app)).Contains("Quit SharpMUTerm?");
    }

    /// <summary>
    /// And the framework's own quit-from-anywhere key is off. It defaults to <c>Ctrl+Q</c> and calls
    /// <c>RequestExit</c> with nothing in between (<c>InputCoordinator</c>); ours wins only because an
    /// application shortcut is tried first, so leaving it set would be a second, unguarded door standing
    /// open behind the confirmation.
    /// </summary>
    [Test]
    public async Task TheFrameworksOwnExitKeyIsOff()
    {
        await Assert.That(App().FrameworkExitKey).IsNull();
    }

    /// <summary>y ends it, and closes the prompt on the way out rather than leaving it painted.</summary>
    [Test]
    public async Task ConfirmingQuits()
    {
        var app = App();
        app.SimulateKey(CtrlQ());

        app.SimulateQuitKey(Key('y', ConsoleKey.Y));

        await Assert.That(app.ExitRequested).IsTrue();
        await Assert.That(app.QuitPromptOpen).IsFalse();
    }

    /// <summary>And every spelling of "no" leaves the client running with the prompt gone.</summary>
    [Test]
    [Arguments('n', ConsoleKey.N)]
    [Arguments('\0', ConsoleKey.Escape)]
    [Arguments('\r', ConsoleKey.Enter)] // ⏎ on the default, which is Stay
    public async Task DecliningDoesNot(char c, ConsoleKey key)
    {
        var app = App();
        app.SimulateKey(CtrlQ());

        app.SimulateQuitKey(new ConsoleKeyInfo(c, key, false, false, false));

        await Assert.That(app.ExitRequested).IsFalse();
        await Assert.That(app.QuitPromptOpen).IsFalse();
    }

    /// <summary>
    /// A second ⌃Q — the impatient double-tap, and what a held chord auto-repeats into — dismisses the
    /// question. It arrives at the global shortcut, not at the modal, so this is the path that has to
    /// agree with <see cref="QuitPrompt.Interpret"/>, and it does.
    /// </summary>
    [Test]
    public async Task ASecondCtrlQDismissesItWithoutQuitting()
    {
        var app = App();
        app.SimulateKey(CtrlQ());

        app.SimulateKey(CtrlQ());

        await Assert.That(app.QuitPromptOpen).IsFalse();
        await Assert.That(app.ExitRequested).IsFalse();
    }

    /// <summary>← moves onto Quit, and ⏎ there means it — the buttons are answerable, not decoration.</summary>
    [Test]
    public async Task PickingQuitWithTheArrowsAndPressingEnterQuits()
    {
        var app = App();
        app.SimulateKey(CtrlQ());

        app.SimulateQuitKey(Bare(ConsoleKey.LeftArrow));
        await Assert.That(Prompt(app)).Contains("⏎ quit");

        app.SimulateQuitKey(Key('\r', ConsoleKey.Enter));

        await Assert.That(app.ExitRequested).IsTrue();
    }

    /// <summary>
    /// The facts come from the live client, not from a fixture: a line typed into the command line and
    /// never sent is named in the question, by the window holding it.
    /// <para>
    /// The window is <c>Corvid</c>, not <c>main</c>. The name here changed with the title a session gives its
    /// own window: it was the demo's stand-in <c>main</c>, which no running client ever showed (a live
    /// session titled that window for its <em>world</em>, so this line would have read
    /// <c>1 unsent draft — Aetherfall</c> directly under <c>1 world connected — Aetherfall</c>). Naming the
    /// character is the same assertion against the shape the client actually has, and a stronger one: it says
    /// <em>which</em> of two characters' half-typed lines is about to be thrown away.
    /// </para>
    /// </summary>
    [Test]
    public async Task TheQuestionNamesTheUnsentDraft()
    {
        var app = App();
        foreach (var c in "look behind the altar")
        {
            app.SimulateKey(Key(c, ConsoleKey.NoName));
        }

        app.SimulateKey(CtrlQ());

        await Assert.That(Prompt(app)).Contains("1 unsent draft — Corvid");
    }

    /// <summary>
    /// And a client holding nothing says exactly that. The frame is drawn from whatever the app hands
    /// over, so "no connections, no drafts" has to survive the gathering as its own sentence.
    /// </summary>
    [Test]
    public async Task AnIdleClientIsToldItHasNothingToLose()
    {
        Console.SetIn(TextReader.Null);
        var app = new SharpMUTermApp(
            new AppConfiguration(), Headless, new HeadlessConsoleDriver(Width, Height));

        app.SimulateKey(CtrlQ());

        await Assert.That(Prompt(app)).Contains("Nothing is connected and nothing is unsent.");
    }

    /// <summary>
    /// An open settings screen is no longer one of the things a quit ends. It used to be named with a
    /// count — "Text &amp; ANSI is open — 1 unsaved edit" — because Esc-ing out of the client discarded
    /// those edits exactly as Esc-ing out of the screen did. Neither key discards anything now: a
    /// committed change is written the moment it is committed, so there is nothing left to warn about and
    /// the prompt says what is actually true.
    /// </summary>
    [Test]
    public async Task AnOpenSettingsScreenIsNotSomethingQuittingCosts()
    {
        var app = App();
        app.DispatchCommand("screen:textansi"); // F7: an options list, every row a checkbox
        app.SimulateSettingsKey(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false));

        app.SimulateKey(CtrlQ());

        await Assert.That(Prompt(app)).DoesNotContain("unsaved");
        await Assert.That(Prompt(app)).DoesNotContain("Text & ANSI");
    }
}
