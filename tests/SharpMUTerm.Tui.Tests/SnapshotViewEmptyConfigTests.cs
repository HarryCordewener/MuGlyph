using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Graphics;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// <b>Every snapshot view survives a configuration with nothing in it.</b>
/// <para>
/// <c>--demo-config</c> is the <em>caller's</em> choice, not <c>RenderSnapshot</c>'s: without it the
/// snapshot renders whatever configuration is on the machine, which on a fresh install is a world list
/// of length zero. A view that reaches for <c>Worlds[0]</c> — or for the first character of a world
/// nobody has put one in — therefore throws out of a path whose whole job is to produce a frame.
/// </para>
/// <para>
/// The <c>characters</c> view did exactly that: it guarded the loop that opened the two sessions and
/// then indexed <c>_config.Worlds[0].Characters[0]</c> unguarded on the line after, to pose the frame.
/// A guard on one indexer and not its neighbour is how this arrives, so this walks <em>every</em> view
/// rather than the one that was reported — the sibling views (<c>quit</c>, the MSSP screen,
/// <c>ActiveWorldIndex</c>) were each already careful, and this is what keeps them that way.
/// </para>
/// </summary>
/// <remarks>
/// Serialised with the other end-to-end suites: constructing the app and rendering a frame both touch the
/// process-global console streams.
/// </remarks>
[NotInParallel]
public class SnapshotViewEmptyConfigTests
{
    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    /// <summary>
    /// Every view <c>--help</c> and <c>docs/</c> name, rendered against a configuration with no worlds at
    /// all. Listed out rather than reflected over the source so that a view added without a thought for
    /// this case is a line somebody has to write here.
    /// </summary>
    private static readonly string[] Views =
    [
        "collapsed", "prefix", "prefix-panel", "timestamps", "timestamps-toggled", "spawn", "split",
        "focus", "focus-moved", "freeze", "freeze-scrollback", "move", "drag", "scrollback",
        "scrollback-up", "web", "rail-long", "history", "history-search", "history-search-filter",
        "draft", "draft2", "menu", "menu-split", "messages", "quit", "connections", "characters",
        "deletions", "textansi", "input", "keypad", "password", "startup", "logging", "set", "triggers",
        "route", "highlight", "worlds", "settings", "mssp", "mssp-none", "mssp-never",
    ];

    /// <summary>
    /// <b>The reported crash, and the sweep behind it.</b> No worlds: the first character of the first
    /// world is not a thing that exists, and no view may assume otherwise.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Views))]
    public async Task AViewRendersAgainstAConfigurationWithNoWorlds(string view)
    {
        Console.SetIn(TextReader.Null);
        var app = new SharpMUTermApp(new AppConfiguration(), Headless, new HeadlessConsoleDriver(120, 34));

        var frame = app.RenderSnapshot(view);

        await Assert.That(frame).IsNotNull().Because($"--view {view} must render against an empty config");
        await Assert.That(frame.Length).IsGreaterThan(0);
    }

    /// <summary>
    /// And the half-populated case the <c>characters</c> view actually tripped over: a world exists, but
    /// nobody has added a character to it. <c>Worlds[0]</c> resolves and <c>Characters[0]</c> does not,
    /// which is the shape a guard on the outer index alone would still miss.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(Views))]
    public async Task AViewRendersAgainstAWorldWithNoCharacters(string view)
    {
        Console.SetIn(TextReader.Null);
        var config = new AppConfiguration();
        config.Worlds.Add(new WorldDefinition { Name = "Bare", Host = "bare.example.org", Port = 4000 });

        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(120, 34));

        var frame = app.RenderSnapshot(view);

        await Assert.That(frame.Length)
            .IsGreaterThan(0)
            .Because($"--view {view} must render against a world nobody has put a character in");
    }
}
