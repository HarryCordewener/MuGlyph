using SharpMUTerm.Core.Diagnostics;

namespace SharpMUTerm.Core.Tests.Diagnostics;

/// <summary>
/// The client message log — where the transient status-line notices are kept once they have dismissed
/// themselves, alongside whatever the telnet stack logged. It is a debugging aid on a client that can
/// run for days, so what matters is that it keeps order and that it stops growing.
/// </summary>
public class ClientMessageLogTests
{
    private static readonly DateTimeOffset Noon = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task RecordsInOrder_WithSeverityAndSource()
    {
        var log = new ClientMessageLog();

        log.Record(Noon, MessageSeverity.Warning, "⌃B nothing to split", "Client");
        log.Record(Noon.AddSeconds(1), MessageSeverity.Error, "could not connect", "Session");

        await Assert.That(log.Entries.Select(e => e.Text))
            .IsEquivalentTo(new[] { "⌃B nothing to split", "could not connect" });
        await Assert.That(log.Entries[0].Severity).IsEqualTo(MessageSeverity.Warning);
        await Assert.That(log.Entries[1].Source).IsEqualTo("Session");
        await Assert.That(log.Entries[1].At).IsEqualTo(Noon.AddSeconds(1));
    }

    /// <summary>
    /// The oldest fall off the end. A capped buffer is the whole reason this is safe to leave running:
    /// the telnet interpreter can log per byte once tracing is raised.
    /// </summary>
    [Test]
    public async Task DropsTheOldest_OnceFull()
    {
        var log = new ClientMessageLog(capacity: 3);

        for (var i = 0; i < 5; i++)
        {
            log.Record(Noon.AddSeconds(i), MessageSeverity.Info, $"message {i}");
        }

        await Assert.That(log.Entries.Count).IsEqualTo(3);
        await Assert.That(log.Entries.Select(e => e.Text))
            .IsEquivalentTo(new[] { "message 2", "message 3", "message 4" });
    }

    [Test]
    public async Task ClearForgetsEverything()
    {
        var log = new ClientMessageLog();
        log.Record(Noon, MessageSeverity.Info, "something");

        log.Clear();

        await Assert.That(log.Entries).IsEmpty();
    }

    [Test]
    public async Task ACapacityBelowOneIsRefused()
    {
        await Assert.That(() => new ClientMessageLog(capacity: 0)).Throws<ArgumentOutOfRangeException>();
    }
}
