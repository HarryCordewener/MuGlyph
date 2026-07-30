using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drivers;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// Pasting into a settings screen's field edit.
/// <para>
/// It did not work at all, and the reason was structural rather than a missed wire: SharpConsoleUI
/// delivers a paste to the active window's <em>focused control</em>, and only when that control is an
/// <c>IPasteTarget</c> — and a settings screen has no editable framework control anywhere on it. The
/// screens are markup rebuilt wholesale on every key, and the field being edited is a buffer inside
/// <see cref="SettingsSession"/> driven from the overlay's <c>PreviewKeyPressed</c>. So every paste
/// aimed at one was dropped. The fix gives the screens the same seam their typing already uses; these
/// hold that seam to the rules typing obeys.
/// </para>
/// </summary>
public class SettingsPasteTests
{
    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

    private static ConsoleKeyInfo Char(char c) => new(c, ConsoleKey.NoName, false, false, false);

    /// <summary>A one-pane screen: a stop, then a record of a host and a port.</summary>
    private sealed class Scene
    {
        public string Host { get; set; } = "aardmud.org";

        public int Port { get; set; } = 4000;

        public SettingsSession Session() => new(_ => new ScreenModel(new[]
        {
            ScreenRow.Stop,
            ScreenRow.Of(
                ScreenField.Text("host", () => Host, v => Host = v),
                ScreenField.Integer("port", () => Port, v => Port = v, 1, 65535)),
        }));
    }

    /// <summary>Opens the host field with the caret at the end of its current value.</summary>
    private static SettingsSession Editing(Scene scene)
    {
        var session = scene.Session();
        session.Handle(Key(ConsoleKey.DownArrow));
        session.Handle(Key(ConsoleKey.Enter));
        return session;
    }

    [Test]
    public async Task APasteLandsInTheOpenFieldAtTheCaret()
    {
        var session = Editing(new Scene());
        session.Handle(Key(ConsoleKey.Home));

        await Assert.That(session.Paste("mud.")).IsEqualTo(ScreenAction.Redraw);

        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("mud.aardmud.org");
        await Assert.That(session.Focus().Edit!.Value.Caret).IsEqualTo(4);
    }

    /// <summary>
    /// A paste with no edit open is not the screen's: there is no text anywhere else on one, and
    /// opening a field on the user's behalf would put a clipboard somewhere they had not chosen.
    /// </summary>
    [Test]
    public async Task APasteWithNoEditOpenIsNotTheScreens()
    {
        var session = new Scene().Session();

        await Assert.That(session.Paste("aardmud.net")).IsEqualTo(ScreenAction.None);
        await Assert.That(session.IsEditing).IsFalse();
    }

    [Test]
    public async Task AnEmptyPasteChangesNothing()
    {
        var session = Editing(new Scene());

        await Assert.That(session.Paste(string.Empty)).IsEqualTo(ScreenAction.None);
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("aardmud.org");
    }

    /// <summary>
    /// A one-line field strips the newlines out of a paste rather than refusing it — the maintainer's
    /// answer — and each run of them collapses to a single space rather than vanishing, because deleting
    /// the break outright would run the words on either side together and quietly change what was
    /// pasted. Every other control character goes, and the result is trimmed: the space a break at
    /// either end would leave is an artefact of the flattening, not something anyone copied.
    /// </summary>
    [Test]
    [Arguments("one\ntwo", "one two")]
    [Arguments("one\r\ntwo", "one two")]
    [Arguments("one\rtwo", "one two")]
    [Arguments("one\n\n\ntwo", "one two")]
    [Arguments("\n\nmiddle\n\n", "middle")]
    [Arguments("tab\there", "tabhere")]
    [Arguments("plain text", "plain text")]
    public async Task AOneLineFieldFlattensThePaste(string pasted, string expected)
    {
        var session = Editing(new Scene());
        session.Handle(Key(ConsoleKey.Home));
        session.Handle(Key(ConsoleKey.Delete));
        while (session.Focus().Edit!.Value.Text.Length > 0)
        {
            session.Handle(Key(ConsoleKey.Delete));
        }

        session.Paste(pasted);

        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo(expected);
    }

    /// <summary>A paste that flattens to nothing at all leaves the buffer alone rather than redrawing.</summary>
    [Test]
    public async Task APasteOfNothingButControlCharactersIsSwallowed()
    {
        var session = Editing(new Scene());

        await Assert.That(session.Paste("\n\r\t")).IsEqualTo(ScreenAction.Consumed);
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("aardmud.org");
    }

    /// <summary>
    /// Stripping newlines is not a validation bypass. A pasted value is committed on the same terms a
    /// typed one is, and a field that refuses it says so and stays open.
    /// </summary>
    [Test]
    public async Task APastedValueIsStillValidatedOnCommit()
    {
        var scene = new Scene();
        var session = Editing(scene);
        session.Handle(Key(ConsoleKey.Tab)); // commit the host, open the port

        while (session.Focus().Edit!.Value.Text.Length > 0)
        {
            session.Handle(Key(ConsoleKey.Backspace));
        }

        session.Paste("not\na port");
        session.Handle(Key(ConsoleKey.Enter));

        await Assert.That(session.IsEditing).IsTrue();
        await Assert.That(session.Focus().Edit!.Value.Error).IsNotNull();
        await Assert.That(scene.Port).IsEqualTo(4000);
    }

    /// <summary>A valid pasted value commits exactly as a typed one does.</summary>
    [Test]
    public async Task AValidPastedValueCommits()
    {
        var scene = new Scene();
        var session = Editing(scene);
        while (session.Focus().Edit!.Value.Text.Length > 0)
        {
            session.Handle(Key(ConsoleKey.Backspace));
        }

        session.Paste("aardmud.net\n");
        session.Handle(Key(ConsoleKey.Enter));

        await Assert.That(session.IsEditing).IsFalse();
        await Assert.That(scene.Host).IsEqualTo("aardmud.net");
    }

    /// <summary>
    /// A key capture (F4's binding field) has no buffer to paste into — its value is a keystroke — so it
    /// swallows a paste for the same reason it swallows typing.
    /// </summary>
    [Test]
    public async Task AKeyCaptureFieldSwallowsAPaste()
    {
        var binding = "F5";
        var session = new SettingsSession(_ => new ScreenModel(new[]
        {
            ScreenRow.Of(ScreenField.Key("key", () => binding, v => binding = v, _ => null)),
        }));
        session.Handle(Key(ConsoleKey.Enter));
        await Assert.That(session.IsEditing).IsTrue();

        await Assert.That(session.Paste("F9")).IsEqualTo(ScreenAction.Consumed);
        await Assert.That(binding).IsEqualTo("F5");
    }

    /// <summary>
    /// The seam itself: a paste arriving at the open overlay reaches the session's buffer and the screen
    /// is rebuilt from it, exactly as a keystroke is. Driven through
    /// <see cref="SettingsOverlay.SimulatePaste"/> — <c>SimulateKey</c>'s counterpart — because the
    /// framework only pumps input inside <c>Run()</c>, which a headless test never enters.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task TheOverlayHandsAPasteToTheOpenScreen()
    {
        Console.SetIn(TextReader.Null);

        var scene = new Scene();
        var session = scene.Session();
        var system = new ConsoleWindowSystem(
            new HeadlessConsoleDriver(120, 34), new ConsoleWindowSystemOptions());
        var overlay = new SettingsOverlay(system);

        overlay.OpenForSnapshot(
            ConsoleKey.F5, new ScreenBinding(session, () => new MarkupControl(new List<string> { "screen" })));
        overlay.SimulateKey(Key(ConsoleKey.DownArrow));
        overlay.SimulateKey(Key(ConsoleKey.Enter));
        await Assert.That(session.IsEditing).IsTrue();

        overlay.SimulatePaste(".net");

        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("aardmud.org.net");
    }

    /// <summary>
    /// ⌃V on these screens reads the local clipboard, because the framework's own ⌃V only fires for a
    /// focused paste target and a screen has none — but it must not steal the chord from a
    /// <see cref="ScreenField.Key"/> capture, where a keystroke <em>is</em> the value being recorded.
    /// Binding ⌃V has to stay possible.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task CtrlV_OverAKeyCaptureIsStillRecordedAsABinding()
    {
        Console.SetIn(TextReader.Null);

        var binding = "F5";
        var session = new SettingsSession(_ => new ScreenModel(new[]
        {
            ScreenRow.Of(ScreenField.Key("key", () => binding, v => binding = v, _ => null)),
        }));
        var system = new ConsoleWindowSystem(
            new HeadlessConsoleDriver(120, 34), new ConsoleWindowSystemOptions());
        var overlay = new SettingsOverlay(system);

        overlay.OpenForSnapshot(
            ConsoleKey.F2, new ScreenBinding(session, () => new MarkupControl(new List<string> { "screen" })));
        overlay.SimulateKey(Key(ConsoleKey.Enter));
        await Assert.That(session.IsCapturingKey).IsTrue();

        overlay.SimulateKey(new ConsoleKeyInfo('', ConsoleKey.V, false, false, true));

        await Assert.That(binding).IsEqualTo("Ctrl+V");
    }

    /// <summary>Typing a character still goes through the same buffer the paste writes to.</summary>
    [Test]
    public async Task TypingAndPastingShareTheSameBuffer()
    {
        var session = Editing(new Scene());

        session.Handle(Char('x'));
        session.Paste("yz");
        session.Handle(Char('!'));

        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("aardmud.orgxyz!");
    }
}
