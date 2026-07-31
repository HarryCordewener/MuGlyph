using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Commands;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Text;
using SharpMUTerm.Core.Workspaces;
using SharpMUTerm.Graphics;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// <b>The upgrade, driven the whole distance.</b> A configuration written by the previous build — schema
/// v4, a saved workspace holding <c>spawn:Chat</c> in a pane — and a restore log written beside it under
/// that same id, opened by this build. The user must lose neither the pane nor the text in it, and the
/// pane must come back as something the client will still write to.
/// <para>
/// The two stores are joined only by the window id, and this change moves it, so they have to be
/// upgraded together or the upgrade is worse than the bug: rewriting only <c>config.json</c> brings the
/// pane back empty for ever, and rewriting only the log leaves the content filed under an id no pane
/// has. <c>ConfigurationMigrator</c>'s v4→v5 step does the first and
/// <c>SharpMUTermApp.CarryLegacySpawnLogsOver</c> does the second, at the launch that reads them.
/// </para>
/// </summary>
/// <remarks>
/// Serialised with the other end-to-end suites: constructing the app and rendering a frame both touch
/// the process-global console streams.
/// </remarks>
[NotInParallel]
public class SpawnWindowIdUpgradeTests
{
    private const int Width = 160;
    private const int Height = 40;
    private const string Corvid = "Aetherfall.Corvid";
    private const string LegacyChat = "spawn:Chat";
    private const string Backlog = "Rivane: anyone up for the crypt run?";

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    private static string CorvidsChat => Workspace.SpawnWindowId(Corvid, "Chat");

    /// <summary>
    /// The headline: the pane is still there, still in its pane, still called <c>Chat</c>, and still
    /// holding the previous session's conversation — under the id its owner now routes to. The user sees
    /// no difference at all, which is the point.
    /// </summary>
    [Test]
    public async Task AnOldConfigKeepsItsPaneItsPlacementAndItsRestoredScrollback()
    {
        using var root = new TempRoot();
        SeedLegacyLog(root);
        var config = OldConfiguration();

        using var log = new RestoreLog(root.Path, config.RestoreLog);
        Console.SetIn(TextReader.Null);
        await using var app = new SharpMUTermApp(
            config, Headless, new HeadlessConsoleDriver(Width, Height), restore: log);

        // The pane, under the id this build routes to — and not under the one it was saved as.
        await Assert.That(app.WindowIds()).Contains(CorvidsChat);
        await Assert.That(app.WindowIds()).DoesNotContain(LegacyChat);
        await Assert.That(app.WindowTitleOf(CorvidsChat)).IsEqualTo("Chat");
        await Assert.That(app.WindowOwnerOf(CorvidsChat)).IsEqualTo(Corvid);

        // Still beside the main window in the pane it was saved in.
        await Assert.That(app.CaptureSession().Root.Tabs).IsEquivalentTo(new[] { "main", CorvidsChat });

        // And holding what it held, closed off by the restore bar.
        var lines = string.Join("\n", app.PaneLines(CorvidsChat));
        await Assert.That(lines).Contains(Backlog);
        await Assert.That(lines).Contains(RestoreBarRenderer.Label);
    }

    /// <summary>
    /// <b>No orphan.</b> The upgraded pane is one the running client still writes to: the character
    /// connects, its capture rule fires, and the line lands in the pane that came back rather than in a
    /// second one beside it. This is the assertion that rules out the tempting cheaper answer of leaving
    /// the old id alone.
    /// </summary>
    [Test]
    public async Task TheUpgradedPaneIsTheOneTheLiveCaptureWritesTo()
    {
        using var root = new TempRoot();
        SeedLegacyLog(root);
        var config = OldConfiguration();

        using var log = new RestoreLog(root.Path, config.RestoreLog);
        Console.SetIn(TextReader.Null);
        await using var app = new SharpMUTermApp(
            config, Headless, new HeadlessConsoleDriver(Width, Height), restore: log);

        var telnet = new RecordingTelnetSession();
        app.TelnetFactory = _ => telnet;
        app.DispatchCommand(CommandIds.Character(Corvid));
        await app.FindSession(Corvid)!.ConnectAsync();

        telnet.Receive("[Chat] Bob: aye, meet me at the gate\n");
        app.RenderNextFrame();

        // One Chat window, and the new line is under the restored history in it.
        await Assert.That(app.WindowIds().Count(id => id.EndsWith(":Chat", StringComparison.Ordinal))).IsEqualTo(1);
        var lines = app.PaneLines(CorvidsChat).ToList();
        await Assert.That(string.Join("\n", lines)).Contains(Backlog);
        await Assert.That(lines[^1]).Contains("meet me at the gate");
    }

    /// <summary>
    /// The carry-over happens once. The second launch reads a log that is already in the new shape, so
    /// the pane comes back holding its history once rather than twice — which is what a mapping kept
    /// for ever, or an old file left on disk beside the new one, would have cost on every launch after
    /// the upgrade.
    /// </summary>
    [Test]
    public async Task TheUpgradeHappensOnceAndDoesNotDoubleThePaneNextLaunch()
    {
        using var root = new TempRoot();
        SeedLegacyLog(root);
        var config = OldConfiguration();

        await Assert.That(root.Files.Any(f => Path.GetFileName(f).StartsWith("spawnChat-", StringComparison.Ordinal)))
            .IsTrue()
            .Because("the previous build's file has to be there for its removal to mean anything");

        using (var first = new RestoreLog(root.Path, config.RestoreLog))
        {
            Console.SetIn(TextReader.Null);
            await using var app = new SharpMUTermApp(
                config, Headless, new HeadlessConsoleDriver(Width, Height), restore: first);
            config.LastSession = app.CaptureSession();
        }

        // The file the old id was in is gone, and the new id has one of its own.
        await Assert.That(root.Files.Any(f => Path.GetFileName(f).StartsWith("spawnChat-", StringComparison.Ordinal)))
            .IsFalse();

        using var second = new RestoreLog(root.Path, config.RestoreLog);
        Console.SetIn(TextReader.Null);
        await using var relaunched = new SharpMUTermApp(
            config, Headless, new HeadlessConsoleDriver(Width, Height), restore: second);

        var lines = relaunched.PaneLines(CorvidsChat).ToList();
        await Assert.That(lines.Count(l => l.Contains(Backlog, StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(lines.Count(l => l.Contains(RestoreBarRenderer.Label, StringComparison.Ordinal)))
            .IsEqualTo(1);
    }

    /// <summary>
    /// A log under an old id that no pane claims is left exactly as it always was: buffered under its
    /// own id, not carried anywhere, not deleted — so if that channel ever speaks again its pane opens
    /// with its history already in it. The upgrade may not turn "the saved workspace forgot this pane"
    /// into "the content is gone".
    /// </summary>
    [Test]
    public async Task AnOldLogNoPaneClaimsIsLeftWhereItIs()
    {
        using var root = new TempRoot();
        using (var seed = new RestoreLog(root.Path))
        {
            seed.Append("spawn:Tells", "Tells", StyledLine.FromText("Rivane pages: hello", TextStyle.Default), "09:24");
        }

        var config = OldConfiguration();
        using var log = new RestoreLog(root.Path, config.RestoreLog);
        Console.SetIn(TextReader.Null);
        await using var app = new SharpMUTermApp(
            config, Headless, new HeadlessConsoleDriver(Width, Height), restore: log);

        await Assert.That(log.Read().Any(w => w.WindowId == "spawn:Tells")).IsTrue();
        await Assert.That(app.WindowIds().Any(id => id.EndsWith(":Tells", StringComparison.Ordinal))).IsFalse();
    }

    /// <summary>
    /// And the fix survives the round trip it is most likely to be undone by. Two characters capture one
    /// target, the workspace is saved and reopened, and each still has a pane of their own holding their
    /// own conversation — the ids are stable across a restart, and the restore log's per-window keying
    /// keeps the two apart on disk as well as in memory.
    /// </summary>
    [Test]
    public async Task TwoCharactersPanesComeBackSeparatelyAfterARestart()
    {
        using var root = new TempRoot();
        var config = SpawnWindowPerSessionTests.Configuration();
        var ann = Workspace.SpawnWindowId("Convergence.Ann", "Public");
        var bob = Workspace.SpawnWindowId("Convergence.Bob", "Public");

        using (var first = new RestoreLog(root.Path, config.RestoreLog))
        {
            Console.SetIn(TextReader.Null);
            await using var app = new SharpMUTermApp(
                config, Headless, new HeadlessConsoleDriver(Width, Height), restore: first);

            foreach (var (key, text) in new[] { ("Convergence.Ann", "first"), ("Convergence.Bob", "second") })
            {
                var telnet = new RecordingTelnetSession();
                app.TelnetFactory = _ => telnet;
                app.DispatchCommand(CommandIds.Character(key));
                await app.FindSession(key)!.ConnectAsync();
                telnet.Receive($"<Public> {key} says, \"{text}\"\n");
                app.RenderNextFrame();
            }

            config.LastSession = app.CaptureSession();
        }

        using var second = new RestoreLog(root.Path, config.RestoreLog);
        Console.SetIn(TextReader.Null);
        await using var relaunched = new SharpMUTermApp(
            config, Headless, new HeadlessConsoleDriver(Width, Height), restore: second);

        await Assert.That(string.Join("\n", relaunched.PaneLines(ann))).Contains("first");
        await Assert.That(string.Join("\n", relaunched.PaneLines(ann))).DoesNotContain("second");
        await Assert.That(string.Join("\n", relaunched.PaneLines(bob))).Contains("second");
        await Assert.That(string.Join("\n", relaunched.PaneLines(bob))).DoesNotContain("first");
    }

    // ---- Harness ----------------------------------------------------------------------------

    /// <summary>A throwaway restore-log root, removed however the test ends.</summary>
    private sealed class TempRoot : IDisposable
    {
        public TempRoot() =>
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"smuterm-upgrade-{Guid.NewGuid():N}");

        public string Path { get; }

        public IReadOnlyList<string> Files =>
            Directory.Exists(Path) ? Directory.GetFiles(Path) : Array.Empty<string>();

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (Exception)
            {
                // Nothing a test should fail over.
            }
        }
    }

    /// <summary>The previous build's restore log: one window, keyed by the id that build wrote.</summary>
    private static void SeedLegacyLog(TempRoot root)
    {
        using var seed = new RestoreLog(root.Path);
        seed.Append(LegacyChat, "Chat", StyledLine.FromText("[Chat] " + Backlog, TextStyle.Default), "09:24");
    }

    /// <summary>
    /// A genuine v4 document, parsed the way the client parses one at startup — so the migration under
    /// test is the one that actually runs, rather than a hand-built object graph that has already had
    /// the answer written into it.
    /// </summary>
    private static AppConfiguration OldConfiguration() => ConfigurationStore.Deserialize("""
    {
      "version": 4,
      "worlds": [ {
        "name": "Aetherfall", "host": "aetherfall.example.org", "port": 4201,
        "characters": [ { "name": "Corvid", "triggerSets": [ "chat" ], "logging": { "format": "None" } } ]
      } ],
      "triggerSets": [ {
        "name": "chat",
        "triggers": [ { "name": "chat", "pattern": "^\\[Chat\\]",
                        "actions": { "spawnTarget": "Chat", "gag": true } } ]
      } ],
      "lastSession": {
        "windows": [
          { "id": "main", "title": "Corvid", "kind": "Main", "sessionKey": "Aetherfall.Corvid" },
          { "id": "spawn:Chat", "title": "Chat", "kind": "Spawn", "sessionKey": "Aetherfall.Corvid",
            "ownerLabel": "Corvid", "capturePattern": "^\\[Chat\\]" }
        ],
        "root": { "type": "pane", "id": "p1", "tabs": [ "main", "spawn:Chat" ], "activeIndex": 0 },
        "focusedPaneId": "p1"
      }
    }
    """);
}
