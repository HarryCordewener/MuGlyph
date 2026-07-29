using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Session;
using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Tests.Session;

/// <summary>
/// The session end of the scrollback spill: that a session gets one, that its history really is
/// deeper than its in-memory window, and that closing the session takes the cache with it — the
/// scrollback cache is not a transcript and must not outlive the window it belonged to.
/// </summary>
public class WorldSessionScrollbackTests
{
    private static WorldDefinition World() => new() { Name = "T", Host = "h", Port = 1 };

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"smuterm-session-spill-{Guid.NewGuid():N}");
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
    public async Task ASessionScrollsBackFurtherThanItsMemoryWindow_AndDropsTheCacheOnClose()
    {
        using var root = new TempRoot();
        var options = new ScrollbackSpillOptions { Directory = root.Path };

        var session = new WorldSession(
            World(),
            sessionFactory: _ => new FakeTelnetSession(),
            scrollbackCapacity: 50,
            spill: options);

        for (var i = 0; i < 500; i++)
        {
            session.PrintSystem($"line {i}");
        }

        await Assert.That(session.Scrollback.Count).IsEqualTo(50);
        await Assert.That(session.Scrollback.TotalLines).IsEqualTo(500L);
        await Assert.That(session.Scrollback.AvailableLines).IsEqualTo(500L);
        await Assert.That(session.Scrollback.IsSpilling).IsTrue();
        await Assert.That(session.Scrollback.GetRange(0, 1)[0].Text).IsEqualTo("line 0");

        // Anything at all under the cache root, which only exists because lines were evicted.
        await Assert.That(Directory.EnumerateDirectories(root.Path).Any()).IsTrue();

        await session.DisposeAsync();
        await Assert.That(Directory.EnumerateDirectories(root.Path).Any()).IsFalse();
    }

    [Test]
    public async Task SpillDisabled_KeepsTheSessionMemoryOnlyAndWritesNothing()
    {
        using var root = new TempRoot();
        var options = new ScrollbackSpillOptions { Directory = root.Path, Enabled = false };

        await using var session = new WorldSession(
            World(),
            sessionFactory: _ => new FakeTelnetSession(),
            scrollbackCapacity: 10,
            spill: options);

        for (var i = 0; i < 100; i++)
        {
            session.PrintSystem($"line {i}");
        }

        await Assert.That(session.Scrollback.IsSpilling).IsFalse();
        await Assert.That(session.Scrollback.AvailableLines).IsEqualTo(10L);
        await Assert.That(Directory.EnumerateFileSystemEntries(root.Path).Any()).IsFalse();
    }
}
