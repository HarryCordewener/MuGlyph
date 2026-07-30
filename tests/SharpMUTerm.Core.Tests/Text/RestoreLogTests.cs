using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Tests.Text;

/// <summary>
/// The kept, per-window record of pane content that refills the panes after a restart: that a window's
/// lines come back under the window's own id, that the bound is enforced in lines and enforced by
/// compaction rather than by rewriting, that a session continues the previous session's log rather
/// than replacing it — and, above all, that <em>nothing</em> a damaged file can be made to look like
/// throws, because the only caller is startup.
/// </summary>
public class RestoreLogTests
{
    private const string Main = "main";
    private const string Chat = "spawn:Chat";

    private static StyledLine Line(string text) => StyledLine.FromText(text, TextStyle.Default);

    /// <summary>A throwaway directory that is removed however the test ends.</summary>
    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"smuterm-restore-{Guid.NewGuid():N}");
        }

        /// <summary>The log root itself — deliberately not created, so "nothing on disk yet" is the start state.</summary>
        public string Path { get; }

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

    private static RestoreLog Open(TempRoot root, int maxLines = 500) =>
        new(root.Path, new RestoreLogOptions { MaxLinesPerWindow = maxLines });

    [Test]
    public async Task DefaultRoot_SitsBesideTheConfigurationAndItsSecrets()
    {
        // The config directory, not a cache one: unlike the spill these files are meant to survive.
        var config = Path.Combine("/somewhere", "SharpMUTerm", "config.json");
        await Assert.That(RestoreLog.DefaultRoot(config))
            .IsEqualTo(Path.Combine("/somewhere", "SharpMUTerm", RestoreLogOptions.DirectoryName));
    }

    [Test]
    public async Task NothingIsWrittenUntilALineIsAppended()
    {
        using var root = new TempRoot();
        using var log = Open(root);

        await Assert.That(Directory.Exists(root.Path)).IsFalse();
        await Assert.That(log.Read()).IsEmpty();
    }

    /// <summary>
    /// The crux the whole design turns on: a spawn window's content never reaches
    /// <c>WorldSession.Scrollback</c>, so the log has to be keyed by window. Two windows, two files, and
    /// each window's lines come back under its own id and nobody else's.
    /// </summary>
    [Test]
    public async Task EachWindowsLinesComeBackUnderItsOwnId()
    {
        using var root = new TempRoot();
        using (var log = Open(root))
        {
            log.Append(Main, "Corvid", Line("The Grand Plaza"), "09:24");
            log.Append(Chat, "Chat", Line("[Chat] Rivane: crypt run?"), "09:25");
            log.Append(Main, "Corvid", Line("A town guard stands watch."), "09:26");
        }

        using var reopened = Open(root);
        var restored = reopened.Read();

        await Assert.That(restored.Count).IsEqualTo(2);

        var main = restored.Single(w => w.WindowId == Main);
        await Assert.That(main.Title).IsEqualTo("Corvid");
        await Assert.That(main.Lost).IsEqualTo(0L);
        await Assert.That(main.Lines.Select(l => l.Line.Text).ToList())
            .IsEquivalentTo(new[] { "The Grand Plaza", "A town guard stands watch." });
        await Assert.That(main.Lines[0].Stamp).IsEqualTo("09:24");

        var chat = restored.Single(w => w.WindowId == Chat);
        await Assert.That(chat.Lines.Count).IsEqualTo(1);
        await Assert.That(chat.Lines[0].Line.Text).IsEqualTo("[Chat] Rivane: crypt run?");
        await Assert.That(chat.Lines[0].Stamp).IsEqualTo("09:25");
    }

    /// <summary>
    /// A window id is not a file name — <c>spawn:Chat</c> holds a colon, and on Windows that is not
    /// merely ugly but illegal. It round-trips because the id is stored in the header, not derived from
    /// the name, and the name carries a checksum so two channels cannot land on one file.
    /// </summary>
    [Test]
    public async Task WindowIdsThatAreNotFileNamesStillRoundTrip()
    {
        using var root = new TempRoot();
        var awkward = new[] { "spawn:Chat", "spawn:Chat/OOC", "spawn:Chat OOC", "spawn:..", "spawn:*" };

        using (var log = Open(root))
        {
            foreach (var id in awkward)
            {
                log.Append(id, id, Line($"in {id}"), null);
            }
        }

        using var reopened = Open(root);
        var restored = reopened.Read();

        await Assert.That(restored.Select(w => w.WindowId).OrderBy(i => i, StringComparer.Ordinal).ToList())
            .IsEquivalentTo(awkward.OrderBy(i => i, StringComparer.Ordinal).ToList());
        foreach (var window in restored)
        {
            await Assert.That(window.Lines.Single().Line.Text).IsEqualTo($"in {window.WindowId}");
        }
    }

    /// <summary>
    /// The style, the palette index, the rule colour and a span's interaction all survive, because the
    /// payload is <see cref="StyledLineCodec"/> and not markup. A restored channel line that came back
    /// monochrome would be a restored line nobody wanted.
    /// </summary>
    [Test]
    public async Task ALinesStyleRuleAndInteractionSurvive()
    {
        using var root = new TempRoot();
        var line = new StyledLine(
            new[]
            {
                new StyledSpan("Exits: ", TextStyle.Default),
                new StyledSpan(
                    "north",
                    new TextStyle(TerminalColor.FromIndex(11), TerminalColor.FromRgb(1, 2, 3), TextAttributes.Underline),
                    SpanInteraction.Command("north")),
            },
            TerminalColor.FromRgb(0x00, 0xf5, 0xb7));

        using (var log = Open(root))
        {
            log.Append(Main, "Corvid", line, "09:24");
        }

        using var reopened = Open(root);
        var restored = reopened.Read().Single().Lines.Single().Line;

        await Assert.That(restored.RuleColor).IsEqualTo(line.RuleColor);
        await Assert.That(restored.Spans.Count).IsEqualTo(2);
        await Assert.That(restored.Spans[1].Style.Foreground).IsEqualTo(TerminalColor.FromIndex(11));
        await Assert.That(restored.Spans[1].Style.Background).IsEqualTo(TerminalColor.FromRgb(1, 2, 3));
        await Assert.That(restored.Spans[1].Style.Attributes).IsEqualTo(TextAttributes.Underline);
        await Assert.That(restored.Spans[1].Interaction!.Kind).IsEqualTo(InteractionKind.SendCommand);
        await Assert.That(restored.Spans[1].Interaction!.Target).IsEqualTo("north");
    }

    /// <summary>
    /// The bound is in lines, and it holds. It is checked well past the compaction threshold so the
    /// answer is not "the file simply never grew": the file is rewritten repeatedly during this and the
    /// newest lines are still the ones that come back, in order.
    /// </summary>
    [Test]
    public async Task OnlyTheNewestLinesUpToTheBoundComeBack()
    {
        using var root = new TempRoot();
        const int bound = 20;

        using (var log = Open(root, bound))
        {
            for (var i = 1; i <= 500; i++)
            {
                log.Append(Main, "Corvid", Line($"line {i}"), null);
            }
        }

        using var reopened = Open(root, bound);
        var lines = reopened.Read().Single().Lines;

        await Assert.That(lines.Count).IsEqualTo(bound);
        await Assert.That(lines[0].Line.Text).IsEqualTo("line 481");
        await Assert.That(lines[^1].Line.Text).IsEqualTo("line 500");
    }

    /// <summary>
    /// And the file is bounded too, not merely the read. Compaction is what stops an append-only log
    /// growing for ever, and it is a byte-range copy through an atomic rename — so after enough appends
    /// the file on disk is small, and no <c>.tmp</c> is left lying about.
    /// </summary>
    [Test]
    public async Task CompactionKeepsTheFileBoundedAndLeavesNoTemporary()
    {
        using var root = new TempRoot();
        const int bound = 10;

        using (var log = Open(root, bound))
        {
            for (var i = 0; i < 40; i++)
            {
                log.Append(Main, "Corvid", Line("a line of quite ordinary length, as they go"), "09:24");
            }

            var files = Directory.GetFiles(root.Path);
            await Assert.That(files.Length).IsEqualTo(1);
            await Assert.That(Path.GetExtension(files[0])).IsEqualTo(".log");

            // Never more than twice the bound's worth of records, whatever the append count.
            await Assert.That(new FileInfo(files[0]).Length).IsLessThan(2 * bound * 200);
        }

        using var reopened = Open(root, bound);
        await Assert.That(reopened.Read().Single().Lines.Count).IsEqualTo(bound);
    }

    /// <summary>
    /// A second run continues the log rather than starting it over. Restarting the client twice in a
    /// minute must not leave a pane holding two lines — which is what truncating on open would do.
    /// </summary>
    [Test]
    public async Task ASecondSessionContinuesTheLogInsteadOfReplacingIt()
    {
        using var root = new TempRoot();

        using (var first = Open(root))
        {
            first.Append(Main, "Corvid", Line("before the restart"), "09:24");
        }

        using (var second = Open(root))
        {
            second.Append(Main, "Corvid", Line("after the restart"), "09:25");
        }

        using var third = Open(root);
        await Assert.That(third.Read().Single().Lines.Select(l => l.Line.Text).ToList())
            .IsEquivalentTo(new[] { "before the restart", "after the restart" });
    }

    /// <summary>
    /// <b>The corruption case, and the one this feature cannot afford to get wrong.</b> A file cut off
    /// mid-record is exactly what a crash leaves behind. Reading it must give back every whole line
    /// before the cut, count the rest as lost, and — the part that matters — not throw into startup.
    /// </summary>
    [Test]
    public async Task ATruncatedFileRestoresWhatIsReadableAndReportsTheRest()
    {
        using var root = new TempRoot();
        using (var log = Open(root))
        {
            for (var i = 1; i <= 6; i++)
            {
                log.Append(Main, "Corvid", Line($"line {i}"), "09:24");
            }
        }

        // Cut the file mid-record: not on a frame boundary, and not merely a byte or two short.
        var path = Directory.GetFiles(root.Path).Single();
        var whole = new FileInfo(path).Length;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            file.SetLength(whole - 11);
        }

        using var reopened = Open(root);
        var restored = reopened.Read().Single();

        await Assert.That(restored.WindowId).IsEqualTo(Main);
        await Assert.That(restored.Lines.Count).IsEqualTo(5);
        await Assert.That(restored.Lines[^1].Line.Text).IsEqualTo("line 5");
        await Assert.That(restored.Lost).IsGreaterThan(0L);

        // And the next session appends onto the trimmed file rather than behind the rubbish.
        reopened.Append(Main, "Corvid", Line("line 7"), "09:25");
        using var again = Open(root);
        await Assert.That(again.Read().Single().Lines.Select(l => l.Line.Text).ToList())
            .IsEquivalentTo(new[] { "line 1", "line 2", "line 3", "line 4", "line 5", "line 7" });
    }

    /// <summary>
    /// One damaged record in the <em>middle</em> costs one line, not every line after it. The framing is
    /// a length prefix and the checksum is separate, so a reader can step over the bad one — which is
    /// the difference between losing a line to a bad sector and losing the session.
    /// </summary>
    [Test]
    public async Task OneDamagedRecordCostsOneLine()
    {
        using var root = new TempRoot();
        using (var log = Open(root))
        {
            for (var i = 1; i <= 5; i++)
            {
                log.Append(Main, "Corvid", Line($"line {i}"), null);
            }
        }

        // Flip a byte inside the third record's payload. Its length prefix is untouched, so the frames
        // after it are still findable; only its own checksum fails.
        var path = Directory.GetFiles(root.Path).Single();
        var bytes = File.ReadAllBytes(path);
        var third = Array.IndexOf(bytes, (byte)'3');
        await Assert.That(third).IsGreaterThan(0); // the fixture found what it meant to damage
        bytes[third] = (byte)'X';
        File.WriteAllBytes(path, bytes);

        using var reopened = Open(root);
        var restored = reopened.Read().Single();

        await Assert.That(restored.Lines.Select(l => l.Line.Text).ToList())
            .IsEquivalentTo(new[] { "line 1", "line 2", "line 4", "line 5" });
        await Assert.That(restored.Lost).IsGreaterThan(0L);
    }

    /// <summary>
    /// Rubbish that is not a log of ours at all — a stray file, a truncated header, or one whose version
    /// this build does not know — is ignored rather than reinterpreted, and never throws.
    /// </summary>
    [Test]
    [Arguments("")]
    [Arguments("not a restore log at all")]
    [Arguments("SMTRSTOR")]
    public async Task RubbishInTheDirectoryIsIgnored(string content)
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(Path.Combine(root.Path, "stray.log"), content);

        using var log = Open(root);
        await Assert.That(log.Read()).IsEmpty();
    }

    [Test]
    public async Task AHeaderFromAFutureVersionIsLeftAloneRatherThanReinterpreted()
    {
        using var root = new TempRoot();
        using (var log = Open(root))
        {
            log.Append(Main, "Corvid", Line("line 1"), null);
        }

        var path = Directory.GetFiles(root.Path).Single();
        var bytes = File.ReadAllBytes(path);
        bytes[8] = 99; // the format version, immediately after the 8-byte magic
        File.WriteAllBytes(path, bytes);

        using var reopened = Open(root);
        await Assert.That(reopened.Read()).IsEmpty();
    }

    /// <summary>
    /// A log whose root cannot be created at all — because a <em>file</em> is sitting where the
    /// directory should be — degrades to writing nothing. It is the shape every permission failure has,
    /// and it must not reach the caller.
    /// </summary>
    [Test]
    public async Task AnUnusableRootDegradesToWritingNothing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"smuterm-restore-blocked-{Guid.NewGuid():N}");
        File.WriteAllText(path, "not a directory");
        try
        {
            using var log = new RestoreLog(path);
            log.Append(Main, "Corvid", Line("line 1"), null);
            await Assert.That(log.Read()).IsEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task PurgeRemovesEveryWindowsLogAndTheLogGoesOnWorking()
    {
        using var root = new TempRoot();
        using var log = Open(root);
        log.Append(Main, "Corvid", Line("line 1"), null);
        log.Append(Chat, "Chat", Line("line 2"), null);

        await Assert.That(log.Purge()).IsEqualTo(2);
        await Assert.That(Directory.Exists(root.Path)).IsFalse();
        await Assert.That(log.Read()).IsEmpty();

        // Purging is "forget what is on disk", not "switch the feature off".
        log.Append(Main, "Corvid", Line("line 3"), null);
        await Assert.That(log.Read().Single().Lines.Single().Line.Text).IsEqualTo("line 3");
    }

    /// <summary>What a character's opt-out does to content already stored: one window's file, gone.</summary>
    [Test]
    public async Task ForgetDropsOneWindowAndLeavesTheOthers()
    {
        using var root = new TempRoot();
        using var log = Open(root);
        log.Append(Main, "Corvid", Line("line 1"), null);
        log.Append(Chat, "Chat", Line("line 2"), null);

        await Assert.That(log.Forget(Main)).IsTrue();
        await Assert.That(log.Forget(Main)).IsFalse(); // already gone; not an error

        var remaining = log.Read();
        await Assert.That(remaining.Count).IsEqualTo(1);
        await Assert.That(remaining[0].WindowId).IsEqualTo(Chat);
    }

    [Test]
    public async Task FilesAreOwnerOnlyLikeTheSecretsBesideThem()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // no Unix mode to assert; the ACL is inherited (see SecretsStore).
        }

        using var root = new TempRoot();
        using var log = Open(root);
        log.Append(Main, "Corvid", Line("what was said in a private game"), null);

        var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await Assert.That(File.GetUnixFileMode(Directory.GetFiles(root.Path).Single())).IsEqualTo(expected);
        await Assert.That(File.GetUnixFileMode(root.Path))
            .IsEqualTo(expected | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// A line nobody could have meant — megabytes with no newline — is stored as a blank row rather than
    /// dropped or written whole. The pane comes back the right length and the file stays bounded.
    /// </summary>
    [Test]
    public async Task APathologicallyLongLineIsKeptAsABlankRow()
    {
        using var root = new TempRoot();
        using (var log = Open(root))
        {
            log.Append(Main, "Corvid", Line("before"), null);
            log.Append(Main, "Corvid", Line(new string('x', 4 * 1024 * 1024)), null);
            log.Append(Main, "Corvid", Line("after"), null);
        }

        using var reopened = Open(root);
        var lines = reopened.Read().Single().Lines;

        await Assert.That(lines.Count).IsEqualTo(3);
        await Assert.That(lines[1].Line.Text).IsEqualTo(string.Empty);
        await Assert.That(lines[2].Line.Text).IsEqualTo("after");
        await Assert.That(new FileInfo(Directory.GetFiles(root.Path).Single()).Length).IsLessThan(1024 * 1024);
    }

    /// <summary>
    /// The disk half of the startup cost, measured rather than asserted about. Six windows at the
    /// shipped bound — 3,000 lines — read and decoded in <b>~2.8 ms</b> on this machine, which is the
    /// I/O-and-decode share of the ~18 ms the whole restore costs
    /// (<c>RestoreLogEndToEndTests.RestoringAFullLogCosts…</c> has the breakdown). The ceiling is two
    /// orders above that because it runs cold in the suite; it still catches a regression into a scan
    /// per line or a re-read per window.
    /// </summary>
    [Test]
    public async Task AFullLogIsReadFastEnoughToRunBeforeTheFirstFrame()
    {
        using var root = new TempRoot();
        var windows = new[] { "main", "spawn:Chat", "spawn:OOC", "spawn:Tells", "spawn:Guild", "spawn:Events" };

        using (var log = Open(root))
        {
            foreach (var window in windows)
            {
                for (var i = 0; i < RestoreLogOptions.DefaultMaxLinesPerWindow; i++)
                {
                    log.Append(window, window, Line($"[{window}] Rivane says something of a fairly typical length here"), "09:24");
                }
            }
        }

        using var reopened = Open(root);
        reopened.Read(); // warm the file cache, so this measures the work and not the first disk touch

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var restored = reopened.Read();
        clock.Stop();

        await Assert.That(restored.Count).IsEqualTo(windows.Length);
        await Assert.That(restored.Sum(w => w.Lines.Count))
            .IsEqualTo(windows.Length * RestoreLogOptions.DefaultMaxLinesPerWindow);
        await Assert.That(clock.ElapsedMilliseconds).IsLessThan(250);
    }
}
