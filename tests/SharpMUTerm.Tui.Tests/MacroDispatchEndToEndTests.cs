using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Graphics;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The wire that was missing. <see cref="MacroEngine"/> and <c>WorldSession.HandleKeyAsync</c> were both
/// written and both unit-tested, and no keystroke had ever reached either of them — so every one of
/// F4's bindings was inert while its own tests passed. These drive real keys into the app's own handler
/// instead, which is the only place that gap was ever visible.
/// </summary>
/// <remarks>
/// Serialised for the same reason <see cref="PaneDragEndToEndTests"/> is: constructing the app touches
/// the process-global console streams, and <c>RenderSnapshot</c> redirects <c>Console.Out</c>.
/// </remarks>
[NotInParallel]
public class MacroDispatchEndToEndTests
{
    private const int Width = 120;
    private const int Height = 34;

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    /// <summary>
    /// The demo configuration with the connected character's session bound but no socket under it, plus
    /// the app driving it. Logging is turned off first: opening the demo character's HTML log would write
    /// a real file into the user's config directory, which a test has no business doing.
    /// </summary>
    private static (SharpMUTermApp App, AppConfiguration Config) Bound()
    {
        Console.SetIn(TextReader.Null);

        var config = DemoScene.Build();
        config.Worlds[0].Characters[0].Logging = new LoggingSettings();

        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
        app.BindWorldWithoutConnecting(config.Worlds[0]);
        return (app, config);
    }

    private static ConsoleKeyInfo Chord(ConsoleKey key, bool ctrl = false, bool alt = false, bool shift = false) =>
        new('\0', key, shift, alt, ctrl);

    /// <summary>
    /// The headline: a configured binding sends its command when its key is pressed. The demo binds
    /// <c>Ctrl+F1</c> to <c>score</c>, which is the one binding in that configuration this host can
    /// actually deliver.
    /// </summary>
    [Test]
    public async Task ABoundKeyFiresItsCommandThroughTheAppsOwnKeyHandler()
    {
        var (app, _) = Bound();

        await Assert.That(app.SimulateKey(Chord(ConsoleKey.F1, ctrl: true))).IsEqualTo("score");
    }

    /// <summary>A key nothing is bound to is not the app's, and is left for whatever wanted it.</summary>
    [Test]
    public async Task AnUnboundKeyDispatchesNothing()
    {
        var (app, _) = Bound();

        await Assert.That(app.SimulateKey(Chord(ConsoleKey.F12, ctrl: true))).IsNull();
    }

    /// <summary>
    /// Rebinding is live. The engine used to be a dictionary keyed on the descriptor string it was handed
    /// at construction, so a macro rebound on F4 went on answering to the key it no longer carried until
    /// the next reconnect — the exact staleness <c>Trigger.Pattern</c> and <c>Alias.CaseSensitive</c>
    /// drop their compiled matcher to avoid.
    /// </summary>
    [Test]
    public async Task RebindingAMacroTakesEffectOnTheNextKeystroke()
    {
        var (app, config) = Bound();
        var macro = config.TriggerSets.SelectMany(s => s.Macros).Single(m => m.Key == "Ctrl+F1");

        macro.Key = "Ctrl+F10";

        await Assert.That(app.SimulateKey(Chord(ConsoleKey.F1, ctrl: true))).IsNull();
        await Assert.That(app.SimulateKey(Chord(ConsoleKey.F10, ctrl: true))).IsEqualTo("score");
    }

    /// <summary>A disabled binding is a row that says it is off, and it is off.</summary>
    [Test]
    public async Task ADisabledBindingDoesNotFire()
    {
        var (app, config) = Bound();
        config.TriggerSets.SelectMany(s => s.Macros).Single(m => m.Key == "Ctrl+F1").Enabled = false;

        await Assert.That(app.SimulateKey(Chord(ConsoleKey.F1, ctrl: true))).IsNull();
    }

    /// <summary>
    /// A settings screen owns the keyboard while it is up: its own ⏎/⇥/Esc are the whole contract, and a
    /// macro firing underneath one would send a command the user was in the middle of editing. This is
    /// the case the framework happens to give us for free (the overlay is a modal window, so the main
    /// window's preview is never raised) and which the app therefore has to assert for itself, because
    /// "for free" is a property of the framework's dispatch and not a decision anyone made here.
    /// </summary>
    [Test]
    public async Task NoMacroFiresWhileASettingsScreenHasTheKeyboard()
    {
        var (app, _) = Bound();
        app.RenderSnapshot("keypad"); // opens the F4 overlay, exactly as the F4 shortcut would

        await Assert.That(app.SimulateKey(Chord(ConsoleKey.F1, ctrl: true))).IsNull();
    }

    /// <summary>The same for the command surface, whose keyboard is a search box.</summary>
    [Test]
    public async Task NoMacroFiresWhileTheCommandSurfaceIsOpen()
    {
        var (app, _) = Bound();
        app.RenderSnapshot("menu");

        await Assert.That(app.SimulateKey(Chord(ConsoleKey.F1, ctrl: true))).IsNull();
    }

    /// <summary>
    /// The other half of the rule, and the one that would ruin the client rather than merely disappoint:
    /// dispatch runs before the prompt sees a key, so an unmodified letter must never be treated as a
    /// binding however a configuration spells it. A macro on a bare key is refused at the F4 capture and
    /// marked on the row; it is refused here too, so a hand-edited file cannot make typing impossible.
    /// </summary>
    [Test]
    public async Task APlainLetterIsTypingEvenWhenAMacroClaimsIt()
    {
        var (app, config) = Bound();
        config.TriggerSets[0].Macros.Add(new Macro { Name = "typo", Key = "K", Command = "kill" });

        await Assert.That(app.SimulateKey(new ConsoleKeyInfo('k', ConsoleKey.K, false, false, false))).IsNull();
    }

    /// <summary>
    /// A macro cannot outrank a chord the app has claimed globally, because a global shortcut runs first
    /// and this handler is never reached — which is exactly what F4 tells the user, and why the two read
    /// the same list (<see cref="MacroKeys.AppShortcuts"/>).
    /// </summary>
    [Test]
    public async Task AMacroOnAChordTheAppClaimsIsNotDispatched()
    {
        var (app, config) = Bound();
        config.TriggerSets[0].Macros.Add(new Macro { Name = "greedy", Key = "Ctrl+Q", Command = "quit" });

        await Assert.That(app.SimulateKey(Chord(ConsoleKey.Q, ctrl: true))).IsNull();
    }

    /// <summary>
    /// The ⌃B pane prefix consumes the next key whole. A macro firing on the key that follows it would
    /// mean the prefix had two meanings depending on what was configured.
    /// </summary>
    [Test]
    public async Task NoMacroFiresOnTheKeyFollowingThePanePrefix()
    {
        var (app, _) = Bound();

        // ⌃B is a global shortcut and never reaches the window handler, so the prefix is armed the way
        // the shortcut arms it — which is what the `prefix` snapshot view does.
        app.RenderSnapshot("prefix");

        await Assert.That(app.SimulateKey(Chord(ConsoleKey.F1, ctrl: true))).IsNull();
    }

    /// <summary>
    /// With nothing connected there is nowhere to send a command, so the key is left alone rather than
    /// swallowed into silence. The bindings live on the session, which is what scopes them to the
    /// character's trigger sets.
    /// </summary>
    [Test]
    public async Task WithNoSessionABoundKeyIsLeftAlone()
    {
        Console.SetIn(TextReader.Null);
        var app = new SharpMUTermApp(DemoScene.Build(), Headless, new HeadlessConsoleDriver(Width, Height));

        await Assert.That(app.SimulateKey(Chord(ConsoleKey.F1, ctrl: true))).IsNull();
    }
}
