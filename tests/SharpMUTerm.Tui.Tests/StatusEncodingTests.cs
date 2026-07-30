using System.Text;
using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Commands;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Telnet;
using SharpMUTerm.Graphics;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The status row's encoding cell reports the encoding <b>in force</b>, not the one configured.
/// <para>
/// It used to draw <c>world.Encoding</c> — a config value presented as fact, and wrong in both
/// directions: the decode path followed CHARSET, so a world configured UTF-8 talking to a server that
/// negotiated Latin-1 read "UTF-8" on the row while decoding Latin-1; and a world on a server that
/// negotiated nothing read "UTF-8" while the telnet layer decoded ASCII. It is the same species as the
/// invented latency figure removed from this row: chrome asserting something it had not measured.
/// </para>
/// <para>
/// The cell is only worth its width because it is live, so it is <em>absent</em> when nothing is
/// connected rather than falling back to the configuration — which is what the address cell did before
/// it was removed for exactly that reason.
/// </para>
/// </summary>
[NotInParallel]
public class StatusEncodingTests
{
    private const int Width = 160;
    private const int Height = 40;

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    /// <summary>
    /// The regression: the world is configured <c>auto</c> and the server negotiated Latin-1, so the
    /// row says Latin-1. Under the old cell it would have said whatever the config field held.
    /// </summary>
    [Test]
    public async Task TheRowReportsTheNegotiatedEncoding_NotTheConfiguredOne()
    {
        var app = await Connected(
            configured: "auto",
            inForce: new SessionEncoding(Encoding.Latin1, EncodingSource.Negotiated));

        await Assert.That(app.StatusMarkup).Contains("iso-8859-1");
        await Assert.That(app.StatusMarkup).DoesNotContain("auto");
    }

    /// <summary>
    /// A world pinned to UTF-8 whose server negotiated Latin-1 reads Latin-1 with no qualifier under
    /// the old cell and "utf-8 forced" under this one — the distinction a user chasing mojibake needs,
    /// because it says whose decision produced the bytes on screen.
    /// </summary>
    [Test]
    public async Task AnOverrideIsMarkedAsOne()
    {
        var app = await Connected(
            configured: "UTF-8",
            inForce: new SessionEncoding(Encoding.UTF8, EncodingSource.Override));

        await Assert.That(app.StatusMarkup).Contains("utf-8 forced");
    }

    /// <summary>A server that never negotiated says so, rather than presenting a guess as agreement.</summary>
    [Test]
    public async Task AnAssumptionIsMarkedAsOne()
    {
        var app = await Connected(
            configured: "auto",
            inForce: new SessionEncoding(Encoding.UTF8, EncodingSource.Assumed));

        await Assert.That(app.StatusMarkup).Contains("utf-8 assumed");
    }

    /// <summary>A negotiated result carries no qualifier — the common case, and the shortest cell.</summary>
    [Test]
    public async Task ANegotiatedResultIsUnqualified()
    {
        var app = await Connected(
            configured: "auto",
            inForce: new SessionEncoding(Encoding.UTF8, EncodingSource.Negotiated));

        await Assert.That(app.StatusMarkup).Contains("utf-8");
        await Assert.That(app.StatusMarkup).DoesNotContain("utf-8 ");
    }

    /// <summary>
    /// With nothing connected there is no encoding to report and the cell is not drawn. Falling back to
    /// the configured value here is the whole bug, one state further along.
    /// </summary>
    [Test]
    public async Task WithNothingConnectedTheCellIsAbsent()
    {
        Console.SetIn(TextReader.Null);
        var config = new AppConfiguration { Worlds = { World("Solo", "ISO-8859-1") } };
        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
        app.RenderNextFrame();

        await Assert.That(app.StatusMarkup).DoesNotContain("iso-8859-1");
        await Assert.That(app.StatusMarkup).DoesNotContain("utf-8");
    }

    /// <summary>
    /// A charset change mid-session repaints the row, because the cell is only worth its width if it
    /// tracks the thing it reports.
    /// </summary>
    [Test]
    public async Task AMidSessionChangeRepaintsTheRow()
    {
        var (app, telnet, clock) = await Connect(
            "auto", new SessionEncoding(Encoding.UTF8, EncodingSource.Assumed));
        clock.Advance(SharpMUTermApp.NoticeDuration + TimeSpan.FromSeconds(1));
        app.RenderNextFrame();
        await Assert.That(app.StatusMarkup).Contains("utf-8 assumed");

        telnet.CurrentEncoding = new SessionEncoding(Encoding.Latin1, EncodingSource.Negotiated);
        telnet.RaiseEncodingChanged();
        app.RenderNextFrame();

        await Assert.That(app.StatusMarkup).Contains("iso-8859-1");
        await Assert.That(app.StatusMarkup).DoesNotContain("assumed");
    }

    /// <summary>
    /// The demo scene declares an encoding, because it builds the <em>connected</em> status row with no
    /// live session behind it — the gap that has now hidden four bugs. This holds the two sides
    /// together the way <c>RailWindowRowTests</c> does for the main window's title: what the demo
    /// declares has to be a state the live writer could really produce, and the cell the snapshot shows
    /// has to be the label <see cref="SessionEncoding"/> produces for it.
    /// </summary>
    [Test]
    public async Task TheDemoSceneDeclaresAnEncodingTheLiveWriterCouldProduce()
    {
        Console.SetIn(TextReader.Null);
        var app = new SharpMUTermApp(
            DemoScene.Build(), Headless, new HeadlessConsoleDriver(Width, Height));
        app.RenderSnapshot();

        var declared = new SessionEncoding(Encoding.UTF8, EncodingSource.Negotiated);
        await Assert.That(app.StatusMarkup).Contains(declared.Label);
        await Assert.That(app.StatusMarkup).DoesNotContain("assumed");
        await Assert.That(app.StatusMarkup).DoesNotContain("forced");

        // And it is a state Aetherfall could reach: the world is on `auto`, so negotiation is free to
        // settle it. A world that pinned an encoding could never report a negotiated result.
        await Assert.That(DemoScene.Build().Worlds[0].Encoding).IsEqualTo("auto");
        await Assert.That(TelnetSessionOptions.ResolveEncoding(DemoScene.Build().Worlds[0].Encoding)).IsNull();
    }

    /// <summary>
    /// The F5 detail column names an override as one. The choice list offers <c>auto</c> first and four
    /// concrete encodings after it, and nothing else on that screen distinguishes "follow the server"
    /// from "ignore the server".
    /// </summary>
    [Test]
    public async Task TheWorldsScreenNamesAnOverrideAsOne()
    {
        await Assert.That(WorldsScreenRenderer.EncodingDetail("auto")).Contains("auto");
        await Assert.That(WorldsScreenRenderer.EncodingDetail("auto")).DoesNotContain("override");
        await Assert.That(WorldsScreenRenderer.EncodingDetail("ISO-8859-1")).Contains("ISO-8859-1");
        await Assert.That(WorldsScreenRenderer.EncodingDetail("ISO-8859-1")).Contains("override");

        // A name this machine cannot resolve is not an override — it behaves as auto everywhere else,
        // and a cell claiming otherwise would be the configured-value bug again.
        await Assert.That(WorldsScreenRenderer.EncodingDetail("not-an-encoding")).Contains("auto");
    }

    private static WorldDefinition World(string name, string encoding) => new()
    {
        Name = name,
        Host = $"{name.ToLowerInvariant()}.example.org",
        Port = 4000,
        Encoding = encoding,
        Characters = { new CharacterDefinition { Name = "Ann", Logging = new LoggingSettings() } },
    };

    /// <summary>
    /// Connects one character and lets the "switched to …" notice retire itself, so what is asserted is
    /// the row's resting content rather than a transient message sitting on top of it. The notice is
    /// dismissed through the same timer the app ships, on the injected clock.
    /// </summary>
    private static async Task<SharpMUTermApp> Connected(string configured, SessionEncoding inForce)
    {
        var (app, _, clock) = await Connect(configured, inForce);
        clock.Advance(SharpMUTermApp.NoticeDuration + TimeSpan.FromSeconds(1));
        app.RenderNextFrame();
        return app;
    }

    private static async Task<(SharpMUTermApp App, RecordingTelnetSession Telnet, ManualTimeProvider Clock)> Connect(
        string configured, SessionEncoding inForce)
    {
        Console.SetIn(TextReader.Null);
        var config = new AppConfiguration { Worlds = { World("Solo", configured) } };
        var clock = new ManualTimeProvider();
        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height), time: clock);
        var telnet = new RecordingTelnetSession { CurrentEncoding = inForce };
        app.TelnetFactory = _ => telnet;

        app.DispatchCommand(CommandIds.Character("Solo.Ann"));
        await app.FindSession("Solo.Ann")!.ConnectAsync();
        app.RenderNextFrame();
        return (app, telnet, clock);
    }
}
