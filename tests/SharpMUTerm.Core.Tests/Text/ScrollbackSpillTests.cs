using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Tests.Text;

/// <summary>
/// The disk half of scrollback: ranged reads across the memory/disk seam, the bound on how much is
/// kept, and — the part that actually matters in a client — every way the disk can fail degrading to
/// memory-only instead of taking a session down with it.
/// </summary>
public class ScrollbackSpillTests
{
    private static StyledLine Line(int index) =>
        StyledLine.FromText($"line {index}", new TextStyle(TerminalColor.FromIndex(index % 256), TerminalColor.Default, TextAttributes.None));

    private static ScrollbackSpillOptions Options(string directory, int maxLines = 100_000, int maxMegabytes = 64, int segmentMegabytes = 4) =>
        new() { Directory = directory, MaxLines = maxLines, MaxMegabytes = maxMegabytes, SegmentMegabytes = segmentMegabytes };

    /// <summary>A throwaway directory that is removed however the test ends.</summary>
    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"smuterm-spill-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Path, recursive: true);
            }
            catch (Exception)
            {
                // Nothing a test should fail over.
            }
        }
    }

    [Test]
    public async Task DefaultRoot_IsACacheLocationAndHonoursXdgCacheHome()
    {
        // A cache directory, never the config or data one: the contents are disposable by design.
        await Assert.That(FileScrollbackSpill.DefaultRoot).Contains("SharpMUTerm");
        await Assert.That(FileScrollbackSpill.DefaultRoot).Contains("scrollback");

        if (OperatingSystem.IsWindows())
        {
            await Assert.That(FileScrollbackSpill.DefaultRoot).Contains("cache");
            return;
        }

        var previous = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", "/xdg-cache-under-test");
            await Assert.That(FileScrollbackSpill.DefaultRoot)
                .IsEqualTo(Path.Combine("/xdg-cache-under-test", "SharpMUTerm", "scrollback"));

            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", null);
            await Assert.That(FileScrollbackSpill.DefaultRoot)
                .Contains(OperatingSystem.IsMacOS() ? Path.Combine("Library", "Caches") : ".cache");
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CACHE_HOME", previous);
        }
    }

    [Test]
    public async Task NothingIsWritten_UntilALineIsActuallyEvicted()
    {
        using var root = new TempRoot();
        using var spill = new FileScrollbackSpill(Options(root.Path));
        using var buffer = new ScrollbackBuffer(capacity: 4, spill);

        buffer.AppendRange(new[] { Line(0), Line(1), Line(2), Line(3) });

        await Assert.That(spill.CacheDirectory).IsNull();
        await Assert.That(Directory.EnumerateFileSystemEntries(root.Path)).IsEmpty();
        await Assert.That(buffer.SpilledLines).IsEqualTo(0L);
    }

    [Test]
    public async Task RangeSpanningTheMemoryDiskBoundary_ReadsBackIntact()
    {
        using var root = new TempRoot();
        using var spill = new FileScrollbackSpill(Options(root.Path));
        using var buffer = new ScrollbackBuffer(capacity: 50, spill);

        for (var i = 0; i < 500; i++)
        {
            buffer.Append(Line(i));
        }

        await Assert.That(buffer.TotalLines).IsEqualTo(500L);
        await Assert.That(buffer.Count).IsEqualTo(50);
        await Assert.That(buffer.SpilledLines).IsEqualTo(450L);
        await Assert.That(buffer.OldestIndex).IsEqualTo(0L);

        // Straddle the seam: 440..469 is 10 lines from disk followed by 20 from memory.
        var straddling = buffer.GetRange(440, 30);
        await Assert.That(straddling).Count().IsEqualTo(30);
        for (var i = 0; i < 30; i++)
        {
            await Assert.That(straddling[i].Text).IsEqualTo($"line {440 + i}");
        }

        // Fully on disk, fully in memory, and the very first and last lines.
        await Assert.That(buffer.GetRange(0, 1)[0].Text).IsEqualTo("line 0");
        await Assert.That(buffer.GetRange(100, 3).Select(l => l.Text)).IsEquivalentTo(new[] { "line 100", "line 101", "line 102" });
        await Assert.That(buffer.GetRange(499, 1)[0].Text).IsEqualTo("line 499");
        await Assert.That(buffer.GetTail(5).Select(l => l.Text))
            .IsEquivalentTo(new[] { "line 495", "line 496", "line 497", "line 498", "line 499" });
    }

    [Test]
    public async Task SpilledLines_KeepTheirStylingAndInteractions()
    {
        using var root = new TempRoot();
        using var spill = new FileScrollbackSpill(Options(root.Path));
        using var buffer = new ScrollbackBuffer(capacity: 2, spill);

        var styled = new StyledLine(
            new[]
            {
                new StyledSpan("你好 ", new TextStyle(TerminalColor.FromRgb(10, 20, 30), TerminalColor.FromIndex(4), TextAttributes.Bold | TextAttributes.Italic)),
                new StyledSpan("👩‍👩‍👧‍👦", TextStyle.Default, SpanInteraction.Command("hug", "Hug them", promptOnly: true)),
            },
            TerminalColor.FromIndex(200));

        buffer.Append(styled);
        buffer.AppendRange(new[] { Line(1), Line(2), Line(3) });

        await Assert.That(buffer.SpilledLines).IsEqualTo(2L);
        var restored = buffer.GetRange(0, 1)[0];
        await Assert.That(restored.Text).IsEqualTo(styled.Text);
        await Assert.That(restored.Spans[0]).IsEqualTo(styled.Spans[0]);
        await Assert.That(restored.Spans[1]).IsEqualTo(styled.Spans[1]);
        await Assert.That(restored.RuleColor).IsEqualTo(styled.RuleColor);
    }

    [Test]
    public async Task RangesOutsideTheAvailableWindow_AreClampedNotRejected()
    {
        using var root = new TempRoot();
        using var spill = new FileScrollbackSpill(Options(root.Path));
        using var buffer = new ScrollbackBuffer(capacity: 8, spill);
        for (var i = 0; i < 40; i++)
        {
            buffer.Append(Line(i));
        }

        await Assert.That(buffer.GetRange(38, 100)).Count().IsEqualTo(2);
        await Assert.That(buffer.GetRange(40, 10)).IsEmpty();
        await Assert.That(buffer.GetRange(1_000, 10)).IsEmpty();
        await Assert.That(buffer.GetRange(0, 0)).IsEmpty();
    }

    [Test]
    public async Task GetRange_RefusesToMaterialiseMoreThanAPage()
    {
        using var buffer = new ScrollbackBuffer(capacity: 10);
        await Assert.That(() => buffer.GetRange(0, ScrollbackBuffer.MaxRangeLines + 1))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => buffer.GetTail(ScrollbackBuffer.MaxRangeLines + 1))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task WithoutASpill_HistoryBeyondTheWindowIsGoneAndIndicesStillMakeSense()
    {
        using var buffer = new ScrollbackBuffer(capacity: 5);
        for (var i = 0; i < 20; i++)
        {
            buffer.Append(Line(i));
        }

        await Assert.That(buffer.IsSpilling).IsFalse();
        await Assert.That(buffer.TotalLines).IsEqualTo(20L);
        await Assert.That(buffer.OldestIndex).IsEqualTo(15L);
        await Assert.That(buffer.AvailableLines).IsEqualTo(5L);
        await Assert.That(buffer.GetRange(0, 100)).Count().IsEqualTo(5);
        await Assert.That(buffer.GetRange(0, 100)[0].Text).IsEqualTo("line 15");
    }

    [Test]
    public async Task TheLineBound_DropsTheOldestHistoryAndReclaimsTheSpace()
    {
        using var root = new TempRoot();

        // ~1 KB a line, so a 1 MB segment holds about a thousand of them and the 5,000-line bound is
        // several segments deep — the granularity the bound is actually enforced at.
        var padding = new string('.', 980);
        using var spill = new FileScrollbackSpill(Options(root.Path, maxLines: 5_000, maxMegabytes: 16, segmentMegabytes: 1));
        using var buffer = new ScrollbackBuffer(capacity: 100, spill);

        for (var i = 0; i < 30_000; i++)
        {
            buffer.Append(StyledLine.FromText($"line {i} {padding}", TextStyle.Default));
        }

        await Assert.That(buffer.TotalLines).IsEqualTo(30_000L);
        await Assert.That(spill.IsHealthy).IsTrue();
        await Assert.That(spill.Count).IsLessThanOrEqualTo(5_000L);
        await Assert.That(spill.Count).IsGreaterThan(2_000L);
        await Assert.That(spill.ByteCount).IsLessThanOrEqualTo(16L * 1024 * 1024);

        // The dropped prefix is genuinely gone and the retained suffix is genuinely intact...
        var oldest = buffer.OldestIndex;
        await Assert.That(oldest).IsGreaterThan(0L);
        await Assert.That(buffer.GetRange(0, 10)).IsEmpty();
        await Assert.That(buffer.GetRange(oldest, 2)[0].Text).StartsWith($"line {oldest} ");
        await Assert.That(buffer.GetRange(oldest, 2)[1].Text).StartsWith($"line {oldest + 1} ");
        await Assert.That(buffer.GetTail(1)[0].Text).StartsWith("line 29999 ");

        // ...and the space really was reclaimed rather than merely forgotten about.
        var onDisk = Directory.EnumerateFiles(spill.CacheDirectory!, "seg-*.bin").Sum(f => new FileInfo(f).Length);
        await Assert.That(onDisk).IsLessThanOrEqualTo(16L * 1024 * 1024);
    }

    [Test]
    public async Task TheByteBound_HoldsEvenWhenTheLineBoundNeverTrips()
    {
        using var root = new TempRoot();
        var padding = new string('.', 980);
        using var spill = new FileScrollbackSpill(Options(root.Path, maxLines: 10_000_000, maxMegabytes: 4, segmentMegabytes: 1));
        using var buffer = new ScrollbackBuffer(capacity: 10, spill);

        for (var i = 0; i < 20_000; i++)
        {
            buffer.Append(StyledLine.FromText($"line {i} {padding}", TextStyle.Default));
        }

        await Assert.That(spill.ByteCount).IsLessThanOrEqualTo(4L * 1024 * 1024);
        await Assert.That(spill.Count).IsGreaterThan(1_000L);
        await Assert.That(buffer.GetRange(buffer.OldestIndex, 1)[0].Text).StartsWith($"line {buffer.OldestIndex} ");
    }

    [Test]
    public async Task ALargeRangeOfLongLines_IsServedAcrossSeveralReads()
    {
        using var root = new TempRoot();
        using var spill = new FileScrollbackSpill(Options(root.Path));
        using var buffer = new ScrollbackBuffer(capacity: 10, spill);

        // 1 KB a line over 1,500 lines is well past the per-read chunk size, so this exercises the
        // multi-pread path rather than the single-shot one every other test takes.
        var padding = new string('#', 1_000);
        for (var i = 0; i < 2_000; i++)
        {
            buffer.Append(StyledLine.FromText($"{i}|{padding}", TextStyle.Default));
        }

        var range = buffer.GetRange(100, 1_500);
        await Assert.That(range).Count().IsEqualTo(1_500);
        for (var i = 0; i < 1_500; i++)
        {
            await Assert.That(range[i].Text).IsEqualTo($"{100 + i}|{padding}");
        }
    }

    [Test]
    public async Task AnAbsurdlyLongLine_IsCachedAsABlankRowWithoutShiftingAnyIndex()
    {
        using var root = new TempRoot();
        using var spill = new FileScrollbackSpill(Options(root.Path));
        using var buffer = new ScrollbackBuffer(capacity: 2, spill);

        // Past the record limit: a server dumping megabytes with no newline in sight.
        buffer.Append(StyledLine.FromText(new string('z', 17 * 1024 * 1024), TextStyle.Default));
        for (var i = 1; i < 6; i++)
        {
            buffer.Append(Line(i));
        }

        var range = buffer.GetRange(0, 4);
        await Assert.That(range).Count().IsEqualTo(4);
        await Assert.That(range[0].IsEmpty).IsTrue();
        await Assert.That(range[1].Text).IsEqualTo("line 1");
        await Assert.That(range[3].Text).IsEqualTo("line 3");
        await Assert.That(spill.IsHealthy).IsTrue();
    }

    [Test]
    public async Task ACorruptRecord_IsDetectedAndReadsBackBlankRatherThanAsGarbage()
    {
        using var root = new TempRoot();
        using var spill = new FileScrollbackSpill(Options(root.Path));
        using var buffer = new ScrollbackBuffer(capacity: 4, spill);
        for (var i = 0; i < 100; i++)
        {
            buffer.Append(Line(i));
        }

        // Force the write buffer out to disk, then flip bytes in the middle of the file.
        _ = buffer.GetRange(0, 10);
        var segment = Directory.EnumerateFiles(spill.CacheDirectory!, "seg-*.bin").Single();
        var bytes = ReadAllBytesShared(segment);
        for (var i = 200; i < 240; i++)
        {
            bytes[i] ^= 0xFF;
        }

        WriteAllBytesShared(segment, bytes);

        var range = buffer.GetRange(0, 96);
        await Assert.That(range).Count().IsEqualTo(96);
        // Indices are still aligned: the damage is localised and the surrounding lines are unharmed.
        await Assert.That(range[0].Text).IsEqualTo("line 0");
        await Assert.That(range[95].Text).IsEqualTo("line 95");
        await Assert.That(range.Count(l => l.IsEmpty)).IsGreaterThan(0);
        await Assert.That(range.Where(l => !l.IsEmpty).All(l => l.Text.StartsWith("line ", StringComparison.Ordinal))).IsTrue();
        await Assert.That(spill.IsHealthy).IsTrue();
    }

    [Test]
    public async Task ATruncatedTrailingRecord_IsDetectedRatherThanCrashing()
    {
        using var root = new TempRoot();
        using var spill = new FileScrollbackSpill(Options(root.Path));
        using var buffer = new ScrollbackBuffer(capacity: 4, spill);
        for (var i = 0; i < 100; i++)
        {
            buffer.Append(Line(i));
        }

        _ = buffer.GetRange(0, 10);
        var segment = Directory.EnumerateFiles(spill.CacheDirectory!, "seg-*.bin").Single();
        var full = new FileInfo(segment).Length;

        // Lop off the tail mid-record, as a crash between a write and its flush would.
        using (var stream = new FileStream(segment, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.SetLength(full - 25);
        }

        var range = buffer.GetRange(0, 96);
        await Assert.That(range).Count().IsEqualTo(96);
        await Assert.That(range[0].Text).IsEqualTo("line 0");
        await Assert.That(range[^1].IsEmpty).IsTrue();
        // Everything before the truncation point still decodes.
        await Assert.That(range.Take(90).All(l => !l.IsEmpty)).IsTrue();
    }

    [Test]
    public async Task AReplacedSegmentFile_IsDetectedByItsHeader()
    {
        using var root = new TempRoot();
        using var spill = new FileScrollbackSpill(Options(root.Path));
        using var buffer = new ScrollbackBuffer(capacity: 4, spill);
        for (var i = 0; i < 50; i++)
        {
            buffer.Append(Line(i));
        }

        _ = buffer.GetRange(0, 4);
        var segment = Directory.EnumerateFiles(spill.CacheDirectory!, "seg-*.bin").Single();
        var bytes = ReadAllBytesShared(segment);
        bytes[0] = (byte)'X';
        WriteAllBytesShared(segment, bytes);

        // The whole segment is now suspect, so every line it held reads back blank — and the range is
        // still the length that was asked for, so a paged view's rows do not shift.
        var range = buffer.GetRange(0, 46);
        await Assert.That(range).Count().IsEqualTo(46);
        await Assert.That(range.All(l => l.IsEmpty)).IsTrue();

        // The in-memory window is untouched by any of this.
        await Assert.That(buffer.GetTail(2)[1].Text).IsEqualTo("line 49");
    }

    [Test]
    public async Task AnUnusableCacheDirectory_DegradesToMemoryOnly()
    {
        using var root = new TempRoot();

        // A file where the store wants a directory: the same class of failure as a read-only
        // filesystem or a denied mkdir, and reproducible on every platform.
        var blocker = Path.Combine(root.Path, "blocked");
        File.WriteAllText(blocker, "not a directory");

        using var spill = new FileScrollbackSpill(Options(Path.Combine(blocker, "spill")));
        using var buffer = new ScrollbackBuffer(capacity: 3, spill);

        for (var i = 0; i < 50; i++)
        {
            buffer.Append(Line(i));
        }

        await Assert.That(buffer.IsSpilling).IsFalse();
        await Assert.That(spill.IsHealthy).IsFalse();

        // Not one live line was lost, and the window still reads correctly.
        await Assert.That(buffer.TotalLines).IsEqualTo(50L);
        await Assert.That(buffer.Count).IsEqualTo(3);
        await Assert.That(buffer.OldestIndex).IsEqualTo(47L);
        await Assert.That(buffer.GetRange(47, 3).Select(l => l.Text))
            .IsEquivalentTo(new[] { "line 47", "line 48", "line 49" });
    }

    [Test]
    public async Task AReadOnlyCacheDirectory_DegradesToMemoryOnly()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return; // POSIX mode bits only; Windows ACLs are a different test.
        }

        using var root = new TempRoot();
        var locked = Path.Combine(root.Path, "readonly");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            using var spill = new FileScrollbackSpill(Options(locked));
            using var buffer = new ScrollbackBuffer(capacity: 2, spill);
            for (var i = 0; i < 20; i++)
            {
                buffer.Append(Line(i));
            }

            await Assert.That(spill.IsHealthy).IsFalse();
            await Assert.That(buffer.TotalLines).IsEqualTo(20L);
            await Assert.That(buffer.GetRange(18, 2)).Count().IsEqualTo(2);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Test]
    public async Task TheCacheVanishingUnderneathUs_DegradesInsteadOfThrowing()
    {
        using var root = new TempRoot();
        using var spill = new FileScrollbackSpill(Options(root.Path, segmentMegabytes: 1));
        using var buffer = new ScrollbackBuffer(capacity: 4, spill);

        var long1 = new string('x', 1500);
        for (var i = 0; i < 20; i++)
        {
            buffer.Append(StyledLine.FromText($"{i}:{long1}", TextStyle.Default));
        }

        var cache = spill.CacheDirectory!;
        Directory.Delete(cache, recursive: true);

        // Keep going until the store needs a new segment — the first operation that has to touch the
        // filesystem by name rather than through a handle it already holds.
        for (var i = 20; i < 1_200; i++)
        {
            buffer.Append(StyledLine.FromText($"{i}:{long1}", TextStyle.Default));
        }

        await Assert.That(buffer.IsSpilling).IsFalse();
        await Assert.That(buffer.TotalLines).IsEqualTo(1_200L);
        await Assert.That(buffer.GetRange(1_196, 4)).Count().IsEqualTo(4);
        await Assert.That(buffer.GetRange(1_199, 1)[0].Text).StartsWith("1199:");
    }

    [Test]
    public async Task Dispose_RemovesTheCacheDirectory()
    {
        using var root = new TempRoot();
        string cache;
        var spill = new FileScrollbackSpill(Options(root.Path));
        using (var buffer = new ScrollbackBuffer(capacity: 2, spill))
        {
            for (var i = 0; i < 20; i++)
            {
                buffer.Append(Line(i));
            }

            cache = spill.CacheDirectory!;
            await Assert.That(Directory.Exists(cache)).IsTrue();
        }

        await Assert.That(Directory.Exists(cache)).IsFalse();
    }

    [Test]
    public async Task Clear_EmptiesBothHalvesAndRestartsIndices()
    {
        using var root = new TempRoot();
        using var spill = new FileScrollbackSpill(Options(root.Path));
        using var buffer = new ScrollbackBuffer(capacity: 3, spill);
        for (var i = 0; i < 30; i++)
        {
            buffer.Append(Line(i));
        }

        buffer.Clear();

        await Assert.That(buffer.Count).IsEqualTo(0);
        await Assert.That(buffer.TotalLines).IsEqualTo(0L);
        await Assert.That(buffer.AvailableLines).IsEqualTo(0L);
        await Assert.That(spill.Count).IsEqualTo(0L);
        await Assert.That(buffer.GetRange(0, 10)).IsEmpty();

        // And it is usable again afterwards.
        for (var i = 0; i < 10; i++)
        {
            buffer.Append(Line(i));
        }

        await Assert.That(buffer.GetRange(0, 10)).Count().IsEqualTo(10);
        await Assert.That(buffer.GetRange(0, 1)[0].Text).IsEqualTo("line 0");
    }

    [Test]
    public async Task PurgeStale_RemovesAbandonedCachesAndLeavesLiveOnesAlone()
    {
        using var root = new TempRoot();
        using var live = new FileScrollbackSpill(Options(root.Path));
        var buffer = new ScrollbackBuffer(capacity: 2, live);
        for (var i = 0; i < 10; i++)
        {
            buffer.Append(Line(i));
        }

        // What a crash leaves: a store directory whose lock nobody holds.
        var abandoned = Path.Combine(root.Path, "store-999999-1-abandoned");
        Directory.CreateDirectory(abandoned);
        File.WriteAllBytes(Path.Combine(abandoned, ".lock"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(abandoned, "seg-000000.bin"), new byte[32]);

        // And something that is emphatically not ours, in case the setting points somewhere silly.
        var innocent = Path.Combine(root.Path, "my-important-notes");
        Directory.CreateDirectory(innocent);
        File.WriteAllText(Path.Combine(innocent, "notes.txt"), "keep me");

        var removed = FileScrollbackSpill.PurgeStale(root.Path);

        await Assert.That(removed).IsEqualTo(1);
        await Assert.That(Directory.Exists(abandoned)).IsFalse();
        await Assert.That(Directory.Exists(innocent)).IsTrue();
        await Assert.That(Directory.Exists(live.CacheDirectory!)).IsTrue();
    }

    [Test]
    public async Task TwoConcurrentStores_DoNotShareAFile()
    {
        using var root = new TempRoot();
        using var first = new FileScrollbackSpill(Options(root.Path), "world.alice");
        using var second = new FileScrollbackSpill(Options(root.Path), "world.alice");
        using var bufferA = new ScrollbackBuffer(capacity: 2, first);
        using var bufferB = new ScrollbackBuffer(capacity: 2, second);

        for (var i = 0; i < 20; i++)
        {
            bufferA.Append(StyledLine.FromText($"a{i}", TextStyle.Default));
            bufferB.Append(StyledLine.FromText($"b{i}", TextStyle.Default));
        }

        await Assert.That(first.CacheDirectory).IsNotEqualTo(second.CacheDirectory);
        await Assert.That(bufferA.GetRange(0, 1)[0].Text).IsEqualTo("a0");
        await Assert.That(bufferB.GetRange(0, 1)[0].Text).IsEqualTo("b0");
    }

    [Test]
    public async Task AppendingWhileReading_IsSafeAndNeverServesATornRange()
    {
        using var root = new TempRoot();
        using var spill = new FileScrollbackSpill(Options(root.Path));
        using var buffer = new ScrollbackBuffer(capacity: 64, spill);

        const int total = 20_000;
        Exception? failure = null;
        using var done = new ManualResetEventSlim(false);

        // The two threads are gated against each other at both ends, and both gates are load-bearing.
        // Appending 20,000 lines takes ~15 ms, which is well inside the time a thread can take to start
        // on a loaded machine: without this the reader could begin after the writer had already finished
        // and the run would report reads == 0 — a measurement of the scheduler, not of the store. It is
        // exactly what a Windows CI run reported once, and it reproduces on Linux by delaying the reader.
        var reads = 0;
        using var readerRunning = new ManualResetEventSlim(false);

        var writer = new Thread(() =>
        {
            try
            {
                readerRunning.Wait(TimeSpan.FromSeconds(10));
                for (var i = 0; i < total; i++)
                {
                    buffer.Append(Line(i));
                }

                // ...and do not retire until the reader has served at least one range. It is still
                // running — `done` is what stops it — so this settles the moment it comes round again,
                // and gives up if it has already fallen over with something for the assertions below.
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (Volatile.Read(ref reads) == 0
                       && Volatile.Read(ref failure) is null
                       && clock.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Thread.Yield();
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        });

        var reader = new Thread(() =>
        {
            readerRunning.Set();
            try
            {
                while (!done.IsSet)
                {
                    var oldest = buffer.OldestIndex;
                    var end = buffer.TotalLines;
                    if (end - oldest < 200)
                    {
                        Thread.Yield();
                        continue;
                    }

                    // A window at depth, chosen while lines are still arriving behind it.
                    var start = oldest + (end - oldest) / 3;
                    var range = buffer.GetRange(start, 40);
                    for (var i = 0; i < range.Count; i++)
                    {
                        if (range[i].Text != $"line {start + i}")
                        {
                            throw new InvalidOperationException(
                                $"Range at {start} returned '{range[i].Text}' at offset {i}");
                        }
                    }

                    Interlocked.Increment(ref reads);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        writer.Start();
        reader.Start();
        writer.Join();
        reader.Join();

        await Assert.That(failure).IsNull();
        await Assert.That(reads).IsGreaterThan(0);
        await Assert.That(buffer.TotalLines).IsEqualTo((long)total);
        await Assert.That(spill.IsHealthy).IsTrue();
    }

    private static byte[] ReadAllBytesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void WriteAllBytesShared(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(bytes);
        stream.SetLength(bytes.Length);
    }
}
