using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Commands;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Graphics;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The command surface's SETTINGS group, checked against the app's own screen table. ⌃P had no way to
/// reach world configuration at all — every settings screen was behind an F-key and nothing on screen
/// said so — and the fix is only worth anything if the surface cannot drift from the keys that are
/// actually registered. So these read <em>both</em> ends: the catalog the palette is built from, and
/// the overlay that opens when one of its ids is dispatched.
/// </summary>
/// <remarks>
/// Serialised for the same reason the other end-to-end suites are: constructing the app touches the
/// process-global console streams.
/// </remarks>
[NotInParallel]
public class CommandSurfaceSettingsTests
{
    private const int Width = 120;
    private const int Height = 34;

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    private static SharpMUTermApp App()
    {
        Console.SetIn(TextReader.Null);
        var config = DemoScene.Build();
        config.Worlds[0].Characters[0].Logging = new LoggingSettings();
        return new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
    }

    private static IReadOnlyList<CommandItem> Settings(SharpMUTermApp app) =>
        app.BuildCatalog().Where(c => c.Group == CommandGroup.Settings).ToList();

    /// <summary>
    /// The one the maintainer asked for: F5 is reachable from ⌃P. Asserted by title <em>and</em> id,
    /// because the id is what dispatch acts on and the title is what a search has to match.
    /// </summary>
    [Test]
    public async Task WorldConfigurationIsOnTheCommandSurface()
    {
        var settings = Settings(App());

        var worlds = settings.Single(c => c.Id == "screen:worlds");
        await Assert.That(worlds.Title).IsEqualTo("Open Worlds & Characters");
        await Assert.That(worlds.Subtitle).IsEqualTo("F5");
    }

    /// <summary>
    /// And all of its neighbours: a surface offering one settings screen and hiding seven would read as
    /// "this is the only thing you can configure". One entry per screen, no more and no fewer.
    /// </summary>
    [Test]
    public async Task EverySettingsScreenIsOffered_AndEachNamesItsOwnFKey()
    {
        var settings = Settings(App());

        await Assert.That(settings.Select(c => c.Id))
            .IsEquivalentTo(new[]
            {
                "screen:triggers", "screen:aliases", "screen:keypad", "screen:worlds",
                "screen:timers", "screen:textansi", "screen:input", "screen:logging",
            });
        await Assert.That(settings.Select(c => c.Subtitle ?? string.Empty))
            .IsEquivalentTo(new[] { "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9" });
    }

    /// <summary>
    /// Dispatching a settings id opens that screen — the same screen its F-key opens, through the same
    /// toggle. Run over every entry, because "the palette lists it" and "the palette opens it" are two
    /// different claims and only the second is the feature.
    /// </summary>
    [Test]
    public async Task DispatchingAnEntryOpensThatScreen()
    {
        var app = App();

        foreach (var (id, key) in new[]
        {
            ("screen:triggers", ConsoleKey.F2), ("screen:aliases", ConsoleKey.F3),
            ("screen:keypad", ConsoleKey.F4), ("screen:worlds", ConsoleKey.F5),
            ("screen:timers", ConsoleKey.F6), ("screen:textansi", ConsoleKey.F7),
            ("screen:input", ConsoleKey.F8), ("screen:logging", ConsoleKey.F9),
        })
        {
            app.DispatchCommand(id);
            await Assert.That(app.OpenSettingsKey).IsEqualTo(key);
            app.DispatchCommand(id); // the same id toggles it shut again, like the F-key does
            await Assert.That(app.OpenSettingsKey).IsNull();
        }
    }

    /// <summary>An id naming no screen is left to the ordinary "not wired yet" path, not silently eaten.</summary>
    [Test]
    public async Task AnUnknownScreenIdOpensNothing()
    {
        var app = App();

        app.DispatchCommand("screen:nonesuch");

        await Assert.That(app.OpenSettingsKey).IsNull();
    }
}
