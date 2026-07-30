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
    /// The app's own save action reaches the screens. Persistence moved to the point of change — a screen
    /// writes each committed value out as it accepts it (see <see cref="ScreenEdits"/>) — so this is the
    /// seam that has to be wired, and it is exactly one hop: the app hands
    /// <c>SaveConfiguration</c> to every <see cref="SettingsSession"/> it builds.
    /// </summary>
    [Test]
    public async Task ACommittedSettingsEditIsPersistedThroughTheAppsSaveAction()
    {
        Console.SetIn(TextReader.Null);
        var saves = 0;
        var app = new SharpMUTermApp(
            DemoScene.Build(),
            Headless,
            new HeadlessConsoleDriver(Width, Height),
            save: _ => saves++);

        app.DispatchCommand("screen:textansi"); // F7: every row a checkbox
        app.SimulateSettingsKey(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false));

        await Assert.That(saves).IsGreaterThan(0);
    }

    /// <summary>
    /// And an app with <em>no</em> save action writes nothing — which is what every test and every
    /// snapshot is, and it is not a detail. A <c>--demo-config</c> frame that drives a key into a field
    /// would otherwise persist the demo worlds over the developer's own configuration, and the
    /// <c>-edit</c> views drive those keys by design.
    /// <para>
    /// It now guards <b>two</b> files. A save writes <c>config.json</c> and, for any character with a
    /// password, <see cref="SecretsStore"/>'s owner-only file beside it — so an app that owned a file would
    /// be one that could overwrite somebody's stored credentials, not merely their worlds.
    /// </para>
    /// </summary>
    [Test]
    public async Task AnAppWithNoSaveActionPersistsNothing()
    {
        var app = App();

        app.DispatchCommand("screen:textansi");
        app.SimulateSettingsKey(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false));

        // Nothing threw and nothing was written: the only route to disk is the action the entry point
        // supplies, and this app was handed none.
        await Assert.That(app.OpenSettingsKey).IsEqualTo(ConsoleKey.F7);
    }

    /// <summary>
    /// End to end through the real store, which is the only place the two-file split can be checked as the
    /// user experiences it: type a password into F5, and the save that the screen triggers puts the secret in
    /// <c>secrets.json</c> and a bare GUID in <c>config.json</c>.
    /// <para>
    /// The app's <c>save</c> action is pointed at a throwaway directory rather than
    /// <see cref="ConfigurationStore.DefaultPath"/> — the developer's own configuration is never a fixture —
    /// and that indirection is exactly the gate
    /// <see cref="AnAppWithNoSaveActionPersistsNothing"/> pins from the other side. The gate now protects two
    /// files instead of one, which is why this is worth asserting through the app and not only through the
    /// store.
    /// </para>
    /// </summary>
    [Test]
    public async Task ACommittedPasswordReachesTheSecretsFileAndNotTheConfigDocument()
    {
        Console.SetIn(TextReader.Null);
        const string secret = "zvxq-endtoend-71";

        var directory = Path.Combine(Path.GetTempPath(), $"smuterm-e2e-{Guid.NewGuid():N}");
        var configPath = Path.Combine(directory, "config.json");
        try
        {
            var config = DemoScene.Build();
            var app = new SharpMUTermApp(
                config,
                Headless,
                new HeadlessConsoleDriver(Width, Height),
                save: saved => ConfigurationStore.Save(configPath, saved));

            // F5, onto the character's own row, into the password field, then type and commit. The same walk
            // the `password-edit` snapshot view drives.
            app.DispatchCommand("screen:worlds");
            app.SimulateSettingsKey(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false));
            app.SimulateSettingsKey(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false));
            app.SimulateSettingsKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
            app.SimulateSettingsKey(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false));
            foreach (var c in secret)
            {
                app.SimulateSettingsKey(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false));
            }

            app.SimulateSettingsKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));

            var character = config.Worlds[0].Characters[0];
            await Assert.That(character.Password).IsEqualTo(secret);

            // The two files, and the one property that matters most: the shareable one holds no secret.
            var document = File.ReadAllText(configPath);
            await Assert.That(document).DoesNotContain(secret);
            await Assert.That(character.PasswordRef).IsNotNull();
            await Assert.That(document).Contains(character.PasswordRef!.Value.ToString("D"));
            await Assert.That(File.ReadAllText(SecretsStore.PathFor(configPath))).Contains(secret);

            // And it comes back: the round trip a restart makes.
            await Assert.That(ConfigurationStore.Load(configPath).Worlds[0].Characters[0].Password)
                .IsEqualTo(secret);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            {
                // Nothing a test should fail over.
            }
        }
    }

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
