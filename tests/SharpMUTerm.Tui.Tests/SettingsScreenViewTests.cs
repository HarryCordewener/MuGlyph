using SharpConsoleUI.Drivers;
using SharpMUTerm.Graphics;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// What a <c>--view</c> name actually opens, driven through the app's own screen table
/// (<c>SettingsScreens()</c>) and rendered headlessly. These are the only tests that see the wiring
/// rather than a renderer in isolation, which is what makes them the place to pin where a settings
/// key goes.
/// </summary>
/// <remarks>
/// Serialised for the same reason <see cref="PaneDragEndToEndTests"/> is: rendering a snapshot
/// redirects <c>Console.Out</c> to capture the frame, and that is process-global.
/// </remarks>
[NotInParallel]
public class SettingsScreenViewTests
{
    private const int Width = 120;
    private const int Height = 34;

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    private static string Frame(string view)
    {
        // The window system reads the console for input even headless; a null reader returns EOF.
        Console.SetIn(TextReader.Null);
        var app = new SharpMUTermApp(DemoScene.Build(), Headless, new HeadlessConsoleDriver(Width, Height));
        return app.RenderSnapshot(view);
    }

    /// <summary>
    /// F9 is a door into F5, not a screen of its own. Its Logging screen edited one character's
    /// <c>LoggingSettings</c> — the active one, or else the first configured — while presenting as a
    /// global preference; the settings now live in the character's form on F5, and the key that used to
    /// open the old screen opens the new home. The <c>logging</c> view name survives with it, because
    /// it is what the snapshot pipeline and the handbook both ask for.
    /// </summary>
    [Test]
    public async Task TheLoggingViewOpensTheWorldsScreenOnTheCharactersLog()
    {
        var frame = Frame("logging");

        await Assert.That(frame).Contains("Worlds & Characters");
        await Assert.That(frame).Contains("CHARACTER · Corvid");

        // The character's own log format and the line that says whose it is.
        await Assert.That(frame).Contains("Html");
        await Assert.That(frame).Contains("this character only");

        // No Logging screen is left to open: the title and the section it used to draw are gone.
        await Assert.That(frame).DoesNotContain("SESSION LOG");
    }

    /// <summary>
    /// The screen names the key that opened it. F5 and F9 open the same screen, and a header that
    /// always said <c>F5</c> would be naming a key that — pressed on the F9-opened screen — re-opens it
    /// somewhere else rather than closing it.
    /// </summary>
    [Test]
    public async Task EachDoorIntoTheWorldsScreenOffersItsOwnKeyToClose()
    {
        await Assert.That(Frame("worlds")).Contains("F5");
        await Assert.That(Frame("logging")).Contains("F9");
    }

    /// <summary>
    /// The <c>-edit</c> variant drives real keys into the screen it opened, so the still frame lands on
    /// the value that view exists for: two steps past the name and the on-connect line, on the log.
    /// </summary>
    [Test]
    public async Task TheLoggingEditViewLandsOnTheLogField()
    {
        var frame = Frame("logging-edit");

        // The block caret is the ink colour painted on the accent; a resting field has no such cell.
        await Assert.That(frame).Contains("Worlds & Characters");
        await Assert.That(Carets(frame)).IsGreaterThan(Carets(Frame("logging")));
    }

    /// <summary>
    /// The <c>set</c> view is F2 stopped on a rule's owning set — the one field on these screens whose
    /// commit moves the row it is made on. The list it offers is <em>closed</em>, because a set is a real
    /// object with characters assigned to it and a name typed here could only be one that does not
    /// exist; the frame is where that presentation is checked rather than merely asserted.
    /// </summary>
    [Test]
    public async Task TheSetViewOpensARulesOwningSetAsAClosedList()
    {
        var frame = Frame("set-edit");

        await Assert.That(frame).Contains("Triggers & spawn routing");
        await Assert.That(frame).Contains(ScreenChrome.ClosedChoicesCaption);

        // Every configured set is offered, including the one holding no triggers at all — which is the
        // whole point of the list being drawn from the configuration rather than from the pane's rows.
        foreach (var set in DemoScene.Build().TriggerSets)
        {
            await Assert.That(frame).Contains(set.Name);
        }

        await Assert.That(Carets(frame)).IsGreaterThan(Carets(Frame("triggers")));
    }

    /// <summary>
    /// How many cells the frame paints in the block-caret colours. Counted rather than matched, because
    /// what distinguishes a field being typed into from the same field at rest is the caret cell.
    /// </summary>
    private static int Carets(string frame)
    {
        var caret = $"{Sgr(ScreenPalette.Ink, 38)};{Sgr(ScreenPalette.Accent, 48)}";
        var count = 0;
        var at = frame.IndexOf(caret, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = frame.IndexOf(caret, at + 1, StringComparison.Ordinal);
        }

        return count;
    }

    /// <summary>A palette hex as the truecolor SGR parameters the driver writes for it.</summary>
    private static string Sgr(string hex, int layer) =>
        $"{layer};2;{Convert.ToInt32(hex.Substring(1, 2), 16)}"
        + $";{Convert.ToInt32(hex.Substring(3, 2), 16)};{Convert.ToInt32(hex.Substring(5, 2), 16)}";
}
