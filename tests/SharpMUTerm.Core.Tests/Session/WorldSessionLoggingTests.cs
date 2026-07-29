using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Logging;
using SharpMUTerm.Core.Session;
using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Tests.Session;

/// <summary>
/// Starting and stopping a session's log while it runs. The command surface offers
/// <c>Start logging</c> / <c>Pause logging</c> and used to be unable to do either: the sink was fixed
/// at construction, so both ids were dispatched into nothing.
/// </summary>
public class WorldSessionLoggingTests
{
    private sealed class RecordingSink : ILogSink
    {
        public List<string> Lines { get; } = new();

        public int Flushes { get; private set; }

        public bool Disposed { get; private set; }

        public void WriteLine(StyledLine line) => Lines.Add(line.Text);

        public void WriteSystem(string text) => Lines.Add(text);

        public void Flush() => Flushes++;

        public void Dispose() => Disposed = true;
    }

    private static WorldSession Session() =>
        new(new WorldDefinition { Name = "T", Host = "h", Port = 1 });

    [Test]
    public async Task AttachedMidSession_TheLogTakesSubsequentLines()
    {
        var session = Session();
        var sink = new RecordingSink();

        session.PrintSystem("*** before");
        await Assert.That(session.IsLogging).IsFalse();

        session.AttachLog(sink);
        session.PrintSystem("*** after");

        await Assert.That(session.IsLogging).IsTrue();
        await Assert.That(sink.Lines).IsEquivalentTo(new[] { "*** after" });
    }

    /// <summary>
    /// Stopping flushes and closes: the file is one a user goes and reads, so anything buffered has to
    /// reach it, and the handle has to be gone once they have been told it stopped.
    /// </summary>
    [Test]
    public async Task DetachingFlushesAndClosesTheSink()
    {
        var session = Session();
        var sink = new RecordingSink();
        session.AttachLog(sink);

        session.DetachLog();

        await Assert.That(session.IsLogging).IsFalse();
        await Assert.That(sink.Flushes).IsEqualTo(1);
        await Assert.That(sink.Disposed).IsTrue();
        session.PrintSystem("*** after");
        await Assert.That(sink.Lines).IsEmpty();
    }

    /// <summary>Detaching twice is safe — "stop" must not depend on knowing whether it started.</summary>
    [Test]
    public async Task DetachingTwiceIsSafe()
    {
        var session = Session();
        var sink = new RecordingSink();
        session.AttachLog(sink);

        session.DetachLog();
        session.DetachLog();

        await Assert.That(sink.Flushes).IsEqualTo(1);
    }

    /// <summary>A second sink closes the first, rather than leaving a handle open on a file said to be stopped.</summary>
    [Test]
    public async Task AttachingASecondSinkClosesTheFirst()
    {
        var session = Session();
        var first = new RecordingSink();
        var second = new RecordingSink();
        session.AttachLog(first);

        session.AttachLog(second);
        session.PrintSystem("*** after");

        await Assert.That(first.Disposed).IsTrue();
        await Assert.That(second.Lines).IsEquivalentTo(new[] { "*** after" });
    }
}
