using System.Diagnostics;
using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Commands;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Session;
using SharpMUTerm.Core.Text;
using SharpMUTerm.Graphics;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// Panes coming back with their previous session's content, driven the whole distance: server bytes into
/// a connected world, through its triggers into a main window and a spawn window, out to the restore log
/// on disk, and back into a <em>second</em> app's panes — which is the only arrangement in which the
/// claim "restarting the client does not empty every pane" can actually be made.
/// <para>
/// <b>The premise the design rests on is pinned first</b>
/// (<see cref="ASpawnWindowsContentNeverReachesTheSessionsScrollback"/>): a spawn window's lines are not
/// in <see cref="WorldSession.Scrollback"/>, so a restore built on session scrollback would bring the
/// main windows back and leave every channel pane empty. That is the whole reason the log is keyed by
/// window id and written from the shell rather than from the session.
/// </para>
/// </summary>
/// <remarks>
/// Serialised with the other end-to-end suites: constructing the app and rendering a frame both touch
/// the process-global console streams.
/// </remarks>
[NotInParallel]
public class RestoreLogEndToEndTests
{
    private const int Width = 160;
    private const int Height = 40;
    private const string MainWindow = "main";
    private const string ChatWindow = "spawn:Chat";

    /// <summary>The startup ceiling asserted below — see that test for the measured figure it guards.</summary>
    private const int Ceiling = 400;

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    /// <summary>A throwaway restore-log root, removed however the test ends.</summary>
    private sealed class TempRoot : IDisposable
    {
        public TempRoot() =>
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"smuterm-restore-e2e-{Guid.NewGuid():N}");

        public string Path { get; }

        public IReadOnlyList<string> Files =>
            System.IO.Directory.Exists(Path) ? System.IO.Directory.GetFiles(Path) : Array.Empty<string>();

        public void Dispose()
        {
            try
            {
                if (System.IO.Directory.Exists(Path))
                {
                    System.IO.Directory.Delete(Path, recursive: true);
                }
            }
            catch (Exception)
            {
                // Nothing a test should fail over.
            }
        }
    }

    // ---- The premise ------------------------------------------------------------------------

    /// <summary>
    /// <b>Why this feature could not have been built on session scrollback.</b> A capture rule that gags
    /// its line — the ordinary way a channel is given a window of its own and kept out of the main
    /// stream — puts that line in the spawn pane and in no session's transcript at all. Restoring
    /// <see cref="WorldSession.Scrollback"/> would therefore refill the main window and hand the reader
    /// an empty Chat pane, which is precisely the failure the whole thing exists to remove.
    /// </summary>
    [Test]
    public async Task ASpawnWindowsContentNeverReachesTheSessionsScrollback()
    {
        using var root = new TempRoot();
        await using var run = await Session(root);

        run.Receive("[Chat] Rivane: anyone up for the crypt run?\n");
        run.Receive("The Grand Plaza.\n");

        // The pane the reader is looking at has it...
        await Assert.That(string.Join("\n", run.App.PaneLines(ChatWindow))).Contains("crypt run");

        // ...and the session's own scrollback — the only per-session history there is — does not.
        var scrollback = string.Join("\n", run.Session.Scrollback.Snapshot().Select(l => l.Text));
        await Assert.That(scrollback).DoesNotContain("crypt run");
        await Assert.That(scrollback).Contains("The Grand Plaza"); // the gag is the rule's, not a bug
    }

    // ---- The headline -----------------------------------------------------------------------

    /// <summary>
    /// The user's ask, end to end: quit, start again, and every pane is holding what it held — the main
    /// window <em>and</em> the spawn window, which is the half a session-scrollback design would have
    /// dropped.
    /// </summary>
    [Test]
    public async Task EveryPaneComesBackIncludingASpawnWindow()
    {
        using var root = new TempRoot();
        var config = Configuration();

        await using (var first = await Session(root, config))
        {
            first.Receive("[Chat] Rivane: anyone up for the crypt run?\n");
            first.Receive("The Grand Plaza.\n");
            config.LastSession = first.App.CaptureSession();
        }

        await using var second = await Restarted(root, config);

        await Assert.That(string.Join("\n", second.App.PaneLines(MainWindow))).Contains("The Grand Plaza");
        await Assert.That(string.Join("\n", second.App.PaneLines(ChatWindow))).Contains("crypt run");
    }

    /// <summary>
    /// Restored content is marked, so a reader can tell where the previous session ended — one bar, at
    /// the boundary, below the restored lines and above whatever arrives next. The <em>lines</em> are
    /// left alone deliberately: restoring is only worth doing if the game's own colours come back with
    /// the text, so the boundary is what is drawn, not the content.
    /// </summary>
    [Test]
    public async Task TheRestoreBarMarksWhereThePreviousSessionEndedAndNothingElse()
    {
        using var root = new TempRoot();
        var config = Configuration();

        await using (var first = await Session(root, config))
        {
            first.Receive("The Grand Plaza.\n");
            config.LastSession = first.App.CaptureSession();
        }

        await using var second = await Restarted(root, config);
        second.Receive("A town guard stands watch.\n");
        var lines = second.App.PaneLines(MainWindow).ToList();

        // Exactly one bar, once, however much arrives after it.
        var bar = lines.FindIndex(l => l.Contains(RestoreBarRenderer.Label, StringComparison.Ordinal));
        await Assert.That(bar).IsGreaterThanOrEqualTo(0);
        await Assert.That(lines.Count(l => l.Contains(RestoreBarRenderer.Label, StringComparison.Ordinal)))
            .IsEqualTo(1);

        // Everything above it is the previous session and everything below it is this one — including
        // the connect banner, which is this session announcing itself and belongs on its own side.
        await Assert.That(lines[bar - 1]).Contains("The Grand Plaza");
        await Assert.That(string.Join("\n", lines[..bar])).DoesNotContain("A town guard stands watch.");
        await Assert.That(string.Join("\n", lines[(bar + 1)..])).Contains("A town guard stands watch.");
        await Assert.That(lines[^1]).Contains("A town guard stands watch.");
    }

    // ---- The gate ---------------------------------------------------------------------------

    /// <summary>
    /// An app handed no restore log writes none — the same gate <c>save</c> and <c>logRoot</c> have, for
    /// the same reason. Every test and every snapshot in this repository gets that default, so a
    /// <c>--demo-config</c> frame cannot spill the demo's panes into the developer's configuration
    /// directory, nor restore the developer's panes into the demo's.
    /// </summary>
    [Test]
    public async Task AnAppWithNoRestoreLogWritesNothing()
    {
        using var root = new TempRoot();
        await using var run = await Session(root, restore: null);

        run.Receive("[Chat] Rivane: anyone up for the crypt run?\n");
        run.Receive("The Grand Plaza.\n");

        await Assert.That(string.Join("\n", run.App.PaneLines(ChatWindow))).Contains("crypt run");
        await Assert.That(Directory.Exists(root.Path)).IsFalse();
    }

    // ---- Where the log and the saved workspace disagree -------------------------------------

    /// <summary>
    /// A window in the log that <c>LastSession</c> no longer holds — a spawn pane that was closed before
    /// quitting — must not throw, must not be lost, and must not be resurrected as a window nobody asked
    /// for. It is buffered instead, so the moment that channel speaks again its pane opens already
    /// holding its history.
    /// </summary>
    [Test]
    public async Task AWindowTheSavedWorkspaceForgotComesBackWhenItsChannelSpeaksAgain()
    {
        using var root = new TempRoot();
        var config = Configuration();

        await using (var first = await Session(root, config))
        {
            first.Receive("[Chat] Rivane: anyone up for the crypt run?\n");

            // Quit with the Chat pane closed: the log knows the window, the saved workspace does not.
            config.LastSession = first.App.CaptureSession();
            config.LastSession.Windows.RemoveAll(w => w.Id == ChatWindow);
        }

        await using var second = await Restarted(root, config);
        await Assert.That(second.App.WindowIds()).DoesNotContain(ChatWindow);

        // One new line reopens the pane — and the history is already in it, above the new line.
        second.Receive("[Chat] Bob: aye, meet me at the gate\n");
        var lines = second.App.PaneLines(ChatWindow).ToList();

        await Assert.That(string.Join("\n", lines)).Contains("crypt run");
        await Assert.That(lines[^1]).Contains("meet me at the gate");
    }

    /// <summary>The other direction: a saved window with no log simply starts empty, and says nothing.</summary>
    [Test]
    public async Task ASavedWindowWithNothingLoggedStartsEmpty()
    {
        using var root = new TempRoot();
        var config = Configuration();

        await using (var first = await Session(root, config))
        {
            first.Receive("[Chat] Rivane: anyone up for the crypt run?\n");
            config.LastSession = first.App.CaptureSession();
        }

        // Drop only the main window's file, keeping Chat's.
        foreach (var file in root.Files.Where(f => Path.GetFileName(f).StartsWith("main-", StringComparison.Ordinal)))
        {
            File.Delete(file);
        }

        await using var second = await Restarted(root, config);

        await Assert.That(second.App.PaneLines(MainWindow)
            .Any(l => l.Contains(RestoreBarRenderer.Label, StringComparison.Ordinal))).IsFalse();
        await Assert.That(string.Join("\n", second.App.PaneLines(ChatWindow))).Contains("crypt run");
    }

    // ---- Damage -----------------------------------------------------------------------------

    /// <summary>
    /// <b>A log a crash left half-written must not take the client down with it.</b> Every file is
    /// truncated mid-record — the shape a kill leaves — and the client still starts, still restores the
    /// whole lines that were there, and still marks them.
    /// </summary>
    [Test]
    public async Task ATruncatedLogRestoresWhatIsReadableAndStillStarts()
    {
        using var root = new TempRoot();
        var config = Configuration();

        await using (var first = await Session(root, config))
        {
            for (var i = 1; i <= 8; i++)
            {
                first.Receive($"line {i} of the plaza\n");
            }

            config.LastSession = first.App.CaptureSession();
        }

        foreach (var file in root.Files)
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Write);
            stream.SetLength(stream.Length - 9); // mid-record, not on a frame boundary
        }

        await using var second = await Restarted(root, config);
        var lines = second.App.PaneLines(MainWindow).ToList();
        var bar = lines.FindIndex(l => l.Contains(RestoreBarRenderer.Label, StringComparison.Ordinal));

        // The whole lines before the cut came back, and the boundary still closes them off.
        await Assert.That(bar).IsGreaterThanOrEqualTo(0);
        await Assert.That(string.Join("\n", lines[..bar])).Contains("line 7 of the plaza");
    }

    /// <summary>And a directory of outright rubbish is startup-safe too, restoring nothing.</summary>
    [Test]
    public async Task RubbishInTheRestoreDirectoryIsStartupSafe()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(Path.Combine(root.Path, "main-deadbeef.log"), "not a restore log at all");

        await using var run = await Session(root);

        await Assert.That(run.App.PaneLines(MainWindow)
            .Any(l => l.Contains(RestoreBarRenderer.Label, StringComparison.Ordinal))).IsFalse();
    }

    // ---- Opting out and purging -------------------------------------------------------------

    /// <summary>
    /// A character who turned <c>restore</c> off on F9 has nothing written — and anything an earlier,
    /// opted-in run left behind is deleted on the next launch rather than merely left undrawn. An
    /// opt-out that kept the file would be answering a different question from the one it was asked.
    /// </summary>
    [Test]
    public async Task ACharacterThatOptedOutIsNeitherLoggedNorRestored()
    {
        using var root = new TempRoot();
        var config = Configuration();

        await using (var first = await Session(root, config))
        {
            first.Receive("The Grand Plaza.\n");
            config.LastSession = first.App.CaptureSession();
        }

        await Assert.That(root.Files.Count).IsGreaterThan(0); // there is something to opt out of
        config.Worlds[0].Characters[0].Logging.RestoreLog = false;

        await using (var second = await Restarted(root, config))
        {
            await Assert.That(second.App.PaneLines(MainWindow)
                .Any(l => l.Contains(RestoreBarRenderer.Label, StringComparison.Ordinal))).IsFalse();

            second.Receive("A town guard stands watch.\n");
        }

        // Neither the old content nor the new: the main window's file is gone and was not rewritten.
        await Assert.That(root.Files.Any(f => Path.GetFileName(f).StartsWith("main-", StringComparison.Ordinal)))
            .IsFalse();
    }

    /// <summary>
    /// ⌃P ▸ <c>Purge the restore log</c> deletes what is on disk, now, and says how much it removed. It
    /// does not blank the panes — what is already drawn is on the reader's screen — and it does not
    /// switch the feature off, so the next line starts a fresh file.
    /// </summary>
    [Test]
    public async Task ThePurgeCommandRemovesEverySavedPaneAndKeepsWorking()
    {
        using var root = new TempRoot();
        await using var run = await Session(root);
        run.Receive("[Chat] Rivane: anyone up for the crypt run?\n");
        run.Receive("The Grand Plaza.\n");
        await Assert.That(root.Files.Count).IsEqualTo(2);

        await Assert.That(run.App.DispatchCommand("term:restore-purge")).IsTrue();

        await Assert.That(root.Files).IsEmpty();
        await Assert.That(run.App.StatusMarkup).Contains("restore log purged");
        await Assert.That(string.Join("\n", run.App.PaneLines(MainWindow))).Contains("The Grand Plaza");

        run.Receive("A town guard stands watch.\n");
        await Assert.That(root.Files.Count).IsEqualTo(1);
    }

    /// <summary>The entry is offered whether or not anything is saved, so it is findable when wanted.</summary>
    [Test]
    public async Task ThePurgeEntryIsInTheCommandSurface()
    {
        using var root = new TempRoot();
        await using var run = await Session(root);

        await Assert.That(run.App.BuildCatalog().Select(c => c.Id)).Contains("term:restore-purge");
    }

    // ---- What it costs at startup -----------------------------------------------------------

    /// <summary>
    /// The startup cost of a full log, measured where it is actually paid: constructing the app, which
    /// is what runs before the first frame. Six windows at the shipped 500-line bound — 3,000 lines, a
    /// busier workspace than most people keep.
    /// <para>
    /// <b>What it costs, measured on this machine.</b> Constructing the app with that log takes ~23 ms
    /// against ~5.5 ms for the same app with no log, so restoring 3,000 lines costs about <b>18 ms</b>,
    /// or 6 µs a line. Reading and decoding them off the disk is only ~2.8 ms of that
    /// (<c>RestoreLogTests.AFullLogIsReadFastEnough…</c>); the rest is rendering each line to markup and
    /// buffering it, which is the same work the line cost when it first arrived. An ordinary two-window
    /// workspace is nearer 6 ms. That is well under a frame and does not need deferring off the startup
    /// path — but it is linear in the bound, which is half the argument for keeping the bound at 500.
    /// </para>
    /// <para>
    /// The ceiling is far above the measured figure on purpose: this runs cold in the suite, and the
    /// claim being defended is "not visible at startup", not a benchmark. It is still tight enough that
    /// a regression into per-line I/O, an fsync per line, or a whole-buffer re-parse per restored line
    /// would trip it by an order of magnitude.
    /// </para>
    /// </summary>
    [Test]
    public async Task RestoringAFullLogCostsLittleEnoughToRunBeforeTheFirstFrame()
    {
        using var root = new TempRoot();
        var config = Configuration();
        var windows = new[] { MainWindow, ChatWindow, "spawn:OOC", "spawn:Tells", "spawn:Guild", "spawn:Events" };

        using (var seed = new RestoreLog(root.Path, config.RestoreLog))
        {
            foreach (var window in windows)
            {
                for (var i = 0; i < RestoreLogOptions.DefaultMaxLinesPerWindow; i++)
                {
                    seed.Append(
                        window,
                        window,
                        StyledLine.FromText($"[{window}] Rivane says something of a typical length", TextStyle.Default),
                        "09:24");
                }
            }
        }

        using var log = new RestoreLog(root.Path, config.RestoreLog);
        Console.SetIn(TextReader.Null);

        var clock = Stopwatch.StartNew();
        await using var app = new SharpMUTermApp(
            config, Headless, new HeadlessConsoleDriver(Width, Height), restore: log);
        clock.Stop();

        await Assert.That(app.PaneLines(MainWindow).Count)
            .IsEqualTo(RestoreLogOptions.DefaultMaxLinesPerWindow + 1); // + the boundary bar
        await Assert.That(clock.ElapsedMilliseconds).IsLessThan(Ceiling);
    }

    // ---- Harness ----------------------------------------------------------------------------

    /// <summary>One connected world, its transport, and the app it prints into.</summary>
    private sealed record Run(SharpMUTermApp App, RecordingTelnetSession Telnet, WorldSession Session, RestoreLog? Log)
        : IAsyncDisposable
    {
        /// <summary>Delivers server text and renders, the way a live read loop and a frame do.</summary>
        public void Receive(string text)
        {
            Telnet.Receive(text);
            App.RenderNextFrame();
        }

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            Log?.Dispose();
        }
    }

    /// <summary>
    /// A configuration with one world, one character, and a capture rule that gags <c>[Chat]</c> into a
    /// spawn window — the ordinary channel arrangement, and the one in which a spawn pane's content
    /// exists nowhere but the pane.
    /// </summary>
    private static AppConfiguration Configuration()
    {
        var config = new AppConfiguration();
        config.TriggerSets.Add(new TriggerSet
        {
            Name = "chat",
            Triggers =
            {
                new Trigger
                {
                    Name = "chat",
                    Pattern = @"^\[Chat\]",
                    Actions = new TriggerActions { SpawnTarget = "Chat", Gag = true },
                },
            },
        });

        config.Worlds.Add(new WorldDefinition
        {
            Name = "Aetherfall",
            Host = "aetherfall.example.org",
            Port = 4201,
            Characters =
            {
                new CharacterDefinition { Name = "Corvid", Logging = new LoggingSettings(), TriggerSets = { "chat" } },
            },
        });

        return config;
    }

    /// <summary>Opens an app over <paramref name="root"/> with its one character connected.</summary>
    private static Task<Run> Session(TempRoot root, AppConfiguration? config = null) =>
        Start(config ?? Configuration(), new RestoreLog(root.Path));

    /// <summary>The same, but with an explicit (possibly null) log — for the "owns no restore log" gate.</summary>
    private static Task<Run> Session(TempRoot root, RestoreLog? restore) => Start(Configuration(), restore);

    /// <summary>
    /// A second launch over the same root and the same configuration: what the user does when they quit
    /// and start the client again.
    /// </summary>
    private static Task<Run> Restarted(TempRoot root, AppConfiguration config) =>
        Start(config, new RestoreLog(root.Path));

    private static async Task<Run> Start(AppConfiguration config, RestoreLog? restore)
    {
        Console.SetIn(TextReader.Null);
        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(Width, Height), restore: restore);
        var telnet = new RecordingTelnetSession();
        app.TelnetFactory = _ => telnet;

        if (!app.DispatchCommand(CommandIds.Character("Aetherfall.Corvid")))
        {
            throw new InvalidOperationException("the app would not switch to Aetherfall.Corvid");
        }

        var session = app.FindSession("Aetherfall.Corvid")!;
        await session.ConnectAsync();
        app.RenderNextFrame();
        return new Run(app, telnet, session, restore);
    }
}
