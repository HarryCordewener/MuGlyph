using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Text;
using SharpMUTerm.Graphics;
using static TUnit.Core.HookType;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The whole-run form of <see cref="LogRootTests"/>'s assertion, and the one that matches how the defect
/// was found: a full suite run left files behind. Every real per-user directory this client knows how to
/// write into is listed before the first test and compared after the last, so no individual test has to
/// remember to check — which is precisely what every test failed to do for as long as this was broken.
/// It lives in a class of its own because TUnit asks global hooks not to share one with tests (TUnit0042).
/// <para>
/// Two trees, covering every real path this client can resolve without being told to:
/// <see cref="ConfigurationStore.DefaultPath"/>'s directory, recursively (so <c>config.json</c>, the
/// owner-only <c>secrets.json</c> beside it, <em>and</em> the <c>logs</c> subdirectory this fix is about),
/// and <see cref="FileScrollbackSpill.DefaultRoot"/>'s cache. The spill has always been correct — it is
/// created on first eviction and no test evicts — but "correct because nothing currently reaches it" is
/// exactly the state the log root was in until a demo character grew a format, so it is watched rather
/// than trusted.
/// </para>
/// </summary>
/// <remarks>
/// One caveat, for whoever reads a failure here: this reads the developer's <em>live</em> directories, so
/// a real client connected in another window and opening its own transcript mid-run would trip it. The
/// message names the files, which is enough to tell that apart from a regression.
/// </remarks>
public static class UserDirectoryGuard
{
    private static readonly string[] Watched =
    [
        Path.GetDirectoryName(ConfigurationStore.DefaultPath)!,
        FileScrollbackSpill.DefaultRoot,
    ];

    private static string[] _baseline = [];

    private static string[] Contents() => Watched
        .Where(Directory.Exists)
        .SelectMany(d => Directory.GetFiles(d, "*", SearchOption.AllDirectories))
        .Order(StringComparer.Ordinal)
        .ToArray();

    [Before(TestSession)]
    public static void Record() => _baseline = Contents();

    /// <summary>
    /// It throws rather than asserting because an after-session hook has no test to fail; TUnit surfaces
    /// the exception as a run failure, which is the outcome wanted.
    /// </summary>
    [After(TestSession)]
    public static void NothingWasWritten()
    {
        var added = Contents().Except(_baseline, StringComparer.Ordinal).ToArray();
        if (added.Length > 0)
        {
            throw new InvalidOperationException(
                $"this test run created {added.Length} file(s) in the developer's own directories: " +
                $"{string.Join(", ", added)}. No app in this suite is given a log root or a save action, " +
                "so none of them may write a transcript, a configuration or a secret.");
        }
    }
}

/// <summary>
/// The gate that stops this suite writing into somebody's real configuration directory, and the
/// assertion that proves it: <b>count the files in the live log directory, and they must not move.</b>
/// <para>
/// The defect. <c>SharpMUTermApp</c> resolved its log folder from the directory of
/// <see cref="ConfigurationStore.DefaultPath"/> unconditionally, so any app that opened a session for a
/// character whose format was not <see cref="LogFormat.None"/> created a real file there. The demo
/// scene's <c>Aetherfall.Corvid</c> is one — <see cref="LogFormat.Html"/>, no directory — so every
/// headless run left transcripts under <c>~/.config/SharpMUTerm/logs</c>, beside genuine ones and beside
/// the client's diagnostics file. 277 of them had accumulated. They were all empty, because nothing was
/// ever connected to write into them; the reach into the data directory was the whole of the harm.
/// </para>
/// <para>
/// The fix mirrors <c>save</c> exactly (see <c>CommandSurfaceSettingsTests.AnAppWithNoSaveActionPersistsNothing</c>):
/// the log root is a constructor parameter, null by default, and only <c>Program</c> supplies the real
/// one. Null means an app that owns no log directory, so it writes no transcript — whatever the
/// character's settings say.
/// </para>
/// <para>
/// <b>Why a file count and not a mock.</b> The property that was violated is "no file appears in that
/// directory". A test asserting <c>LogFolder</c> returns null, or that some fake sink was not opened,
/// would agree with the code and reproduce the blind spot: the old code was internally consistent and
/// still wrote 277 files. This counts the directory itself, before and after, including across the whole
/// test session (see <see cref="UserDirectoryGuard"/>) — the one measurement that could not have passed
/// while the defect was live.
/// </para>
/// </summary>
/// <remarks>
/// Serialised with the other end-to-end suites: constructing the app touches the process-global console
/// streams.
/// </remarks>
[NotInParallel]
public class LogRootTests
{
    private const int Width = 120;
    private const int Height = 34;

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    /// <summary>
    /// The directory the live client writes transcripts into — resolved here the same way
    /// <c>Program</c> resolves it, because this test's whole subject is whether anything else reaches
    /// for it. Reading a count out of it is the only way to assert that; nothing here writes to it.
    /// </summary>
    private static string LiveLogDirectory =>
        Path.Combine(Path.GetDirectoryName(ConfigurationStore.DefaultPath)!, "logs");

    private static int LiveLogFileCount() =>
        Directory.Exists(LiveLogDirectory) ? Directory.GetFiles(LiveLogDirectory).Length : 0;

    /// <summary>
    /// The per-test form, driving the exact path that leaked: the demo worlds as configured — Corvid on
    /// <see cref="LogFormat.Html"/> — bound into an app, then told to start logging as well. Before the
    /// gate this produced two files in the live directory on its own.
    /// </summary>
    [Test]
    public async Task BindingTheDemoWorldsWritesNothingIntoTheLiveLogDirectory()
    {
        Console.SetIn(TextReader.Null);
        var before = LiveLogFileCount();

        var config = DemoScene.Build();

        // The setting the defect rode in on, asserted rather than assumed: if the demo ever stops
        // configuring a format, this test goes quiet without saying so.
        await Assert.That(config.Worlds[0].Characters[0].Logging.Format).IsEqualTo(LogFormat.Html);
        await Assert.That(config.Worlds[0].Characters[0].Logging.Directory).IsNull();

        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
        foreach (var world in config.Worlds)
        {
            app.BindWorldWithoutConnecting(world);
        }

        app.DispatchCommand("term:log-on");
        await app.LastCommand;

        await Assert.That(LiveLogFileCount())
            .IsEqualTo(before)
            .Because("an app with no log root owns no log directory and may not write into anyone's");
    }

    /// <summary>
    /// And an explicit <see cref="LoggingSettings.Directory"/> does not reopen the door. The root is the
    /// app's answer to "may I write transcripts at all"; a character's directory only ever chose where
    /// within that. Read the other way, every fixture naming a path would be free to write outside itself
    /// — which is how a test suite ends up creating files nobody asked for, one absolute path at a time.
    /// </summary>
    [Test]
    public async Task AnExplicitCharacterDirectoryIsRefusedToo()
    {
        Console.SetIn(TextReader.Null);
        var directory = Path.Combine(Path.GetTempPath(), $"smuterm-logroot-{Guid.NewGuid():N}");

        var config = DemoScene.Build();
        foreach (var character in config.Worlds.SelectMany(w => w.Characters))
        {
            character.Logging = new LoggingSettings { Format = LogFormat.Both, Directory = directory };
        }

        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
        app.BindWorldWithoutConnecting(config.Worlds[0]);
        app.DispatchCommand("term:log-on");
        await app.LastCommand;

        // Not merely empty — never created. The sinks make their own directory on the way to the file.
        await Assert.That(Directory.Exists(directory)).IsFalse();
    }

    /// <summary>
    /// The other half, without which the gate could be "logging is simply broken" and nothing here would
    /// notice: an app handed a log root writes the transcript the character asked for, into it.
    /// </summary>
    [Test]
    public async Task AnAppGivenALogRootWritesTheTranscriptThere()
    {
        Console.SetIn(TextReader.Null);
        var root = Path.Combine(Path.GetTempPath(), $"smuterm-logroot-{Guid.NewGuid():N}");
        try
        {
            var config = DemoScene.Build();
            var app = new SharpMUTermApp(
                config, Headless, new HeadlessConsoleDriver(Width, Height), logRoot: root);
            app.BindWorldWithoutConnecting(config.Worlds[0]);

            // Corvid is Html, so that is the extension the live path produces.
            var written = Directory.GetFiles(root);
            await Assert.That(written.Length).IsEqualTo(1);
            await Assert.That(Path.GetFileName(written[0])).StartsWith("Aetherfall.Corvid-");
            await Assert.That(Path.GetExtension(written[0])).IsEqualTo(".html");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // ---- What the surfaces say when there is no root ----------------------------------------

    /// <summary>
    /// The honesty half. The header's <c>LOG</c> cell reported the active character's <em>configured</em>
    /// format, so a logless app rendered <c>LOG html</c> over a client that had opened no file and had
    /// nowhere to open one. It reads <c>LOG off</c> now, which is what is happening. Same reasoning
    /// <see cref="SharpMUTerm.Core.Session.WorldSession.CurrentEncoding"/> is built on: a configured value
    /// is a preference, and a status cell may not report one as though it were in force.
    /// </summary>
    [Test]
    public async Task TheHeaderSaysLoggingIsOffWhenThereIsNoLogRoot()
    {
        Console.SetIn(TextReader.Null);
        var config = DemoScene.Build();
        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
        app.BindWorldWithoutConnecting(config.Worlds[0]);

        await Assert.That(app.HeaderText).Contains("LOG off");
        await Assert.That(app.HeaderText).DoesNotContain("LOG html");
    }

    /// <summary>The same app, given a root, does say what it is writing — so the cell is not simply dead.</summary>
    [Test]
    public async Task TheHeaderNamesTheFormatWhenThereIsOne()
    {
        Console.SetIn(TextReader.Null);
        var root = Path.Combine(Path.GetTempPath(), $"smuterm-logroot-{Guid.NewGuid():N}");
        try
        {
            var config = DemoScene.Build();
            var app = new SharpMUTermApp(
                config, Headless, new HeadlessConsoleDriver(Width, Height), logRoot: root);
            app.BindWorldWithoutConnecting(config.Worlds[0]);

            await Assert.That(app.HeaderText).Contains("LOG html");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// <c>⌃P ▸ Start logging</c> refuses out loud rather than doing nothing quietly or printing
    /// <c>*** Logging to …</c> over a file that was never opened. Refusing is the pattern this client
    /// already uses wherever a command has nothing to act on (see
    /// <c>CommandDispatchTests.DisconnectWithNothingConnected_SaysSo</c>), and it keeps the entry's own
    /// label honest: nothing started, so the surface still offers <c>Start logging</c>.
    /// </summary>
    [Test]
    public async Task StartLoggingRefusesOutLoudWhenThereIsNoLogRoot()
    {
        Console.SetIn(TextReader.Null);
        var config = DemoScene.Build();
        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height));
        var session = app.BindWorldWithoutConnecting(config.Worlds[0]);

        app.DispatchCommand("term:log-on");
        await app.LastCommand;

        await Assert.That(app.StatusMarkup).Contains("owns no log directory");
        await Assert.That(session.IsLogging).IsFalse();
        await Assert.That(app.BuildCatalog().Any(c => c.Id == "term:log-on")).IsTrue();
        await Assert.That(app.BuildCatalog().Any(c => c.Id == "term:log-off")).IsFalse();
    }
}
