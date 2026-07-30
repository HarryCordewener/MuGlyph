using SharpMUTerm.Core.Telnet.Mssp;
using SharpMUTerm.Crawler.Model;
using SharpMUTerm.Crawler.Scheduling;
using SharpMUTerm.Crawler.Tests.Support;

namespace SharpMUTerm.Crawler.Tests;

/// <summary>
/// What the crawler remembers about a host that failed, and when it is willing to try again.
/// </summary>
public class BackoffTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static MsspHost Host(string name = "a.example.org", int port = 4201) => MsspHost.Create(name, port)!;

    private static ProbeResult Result(MsspHost host, CrawlOutcome outcome, MsspData? data = null, DateTimeOffset? at = null) =>
        new()
        {
            Host = host,
            Outcome = outcome,
            ObservedAt = at ?? Now,
            Data = data,
            Error = outcome is CrawlOutcome.ConnectFailed ? "ConnectionRefused" : null,
        };

    private static (CrawlFrontier Frontier, MsspHost Host) Seeded(CrawlOptions? options = null)
    {
        var frontier = new CrawlFrontier(options ?? new CrawlOptions());
        var host = Host();
        frontier.AddSeed(host, Now);
        return (frontier, host);
    }

    [Test]
    public async Task ARefusedConnectionIsRecordedAndNotRetriedImmediately()
    {
        var (frontier, host) = Seeded();

        frontier.Record(Result(host, CrawlOutcome.ConnectFailed));
        var record = frontier.Records.Single();

        await Assert.That(record.ConsecutiveFailures).IsEqualTo(1);
        await Assert.That(record.LastOutcome).IsEqualTo(CrawlOutcome.ConnectFailed);
        await Assert.That(record.LastError).IsEqualTo("ConnectionRefused");
        await Assert.That(record.NotBefore).IsEqualTo(Now + TimeSpan.FromHours(1));
        await Assert.That(record.IsDue(Now)).IsFalse();
        await Assert.That(record.IsDue(Now + TimeSpan.FromHours(1))).IsTrue();

        // And the frontier will not hand it out again while it is not due.
        await Assert.That(frontier.TakeNext(Now)).IsNull();
    }

    [Test]
    public async Task EachFurtherFailureWaitsLonger()
    {
        var (frontier, host) = Seeded();
        var expected = new[] { 1d, 6d, 24d, 72d };

        var at = Now;
        for (var attempt = 0; attempt < expected.Length; attempt++)
        {
            frontier.Record(Result(host, CrawlOutcome.ConnectFailed, at: at));
            var record = frontier.Records.Single();
            await Assert.That(record.NotBefore).IsEqualTo(at + TimeSpan.FromHours(expected[attempt]));

            at = record.NotBefore!.Value;
            // Re-claim it, the way the crawl loop does, so the next Record has something to release.
            frontier.TakeNext(at);
        }
    }

    [Test]
    public async Task RepeatedFailuresRetireAHostEntirely()
    {
        var (frontier, host) = Seeded(new CrawlOptions { RetireAfterFailures = 3 });

        for (var i = 0; i < 3; i++)
        {
            frontier.Record(Result(host, CrawlOutcome.ConnectFailed));
            frontier.TakeNext(Now + TimeSpan.FromDays(30));
        }

        var record = frontier.Records.Single();
        await Assert.That(record.Retired).IsTrue();
        await Assert.That(record.IsDue(Now + TimeSpan.FromDays(365))).IsFalse();

        // Retired means retired: the frontier never offers it again, however long a run waits.
        await Assert.That(frontier.TakeNext(Now + TimeSpan.FromDays(365))).IsNull();
    }

    [Test]
    public async Task OneSuccessClearsTheFailureCount()
    {
        var (frontier, host) = Seeded();

        frontier.Record(Result(host, CrawlOutcome.ConnectFailed));
        frontier.TakeNext(Now + TimeSpan.FromHours(2));
        frontier.Record(Result(host, CrawlOutcome.MsspReceived, MsspData.Empty, Now + TimeSpan.FromHours(2)));

        var record = frontier.Records.Single();
        await Assert.That(record.ConsecutiveFailures).IsEqualTo(0);
        await Assert.That(record.LastSuccess).IsEqualTo(Now + TimeSpan.FromHours(2));
    }

    [Test]
    public async Task AServerWithNoMsspIsNotAFailureButWaitsMuchLonger()
    {
        // Most MU* servers do not implement MSSP. That is not a fault of theirs and must not retire
        // them, but asking again tomorrow would be pure cost to both sides.
        var (frontier, host) = Seeded();

        frontier.Record(Result(host, CrawlOutcome.NoMssp));
        var record = frontier.Records.Single();

        await Assert.That(record.ConsecutiveFailures).IsEqualTo(0);
        await Assert.That(record.Retired).IsFalse();
        await Assert.That(record.NotBefore).IsEqualTo(Now + TimeSpan.FromDays(7));
    }

    [Test]
    public async Task ASuccessfulCrawlSetsTheRevisitInterval()
    {
        var (frontier, host) = Seeded();

        frontier.Record(Result(host, CrawlOutcome.MsspReceived, MsspData.Empty));
        var record = frontier.Records.Single();

        await Assert.That(record.NotBefore).IsEqualTo(Now + TimeSpan.FromHours(24));
        await Assert.That(record.IsDue(Now + TimeSpan.FromHours(23))).IsFalse();
        await Assert.That(record.IsDue(Now + TimeSpan.FromHours(24))).IsTrue();
    }

    [Test]
    public async Task AServerAskingForALongerCrawlDelayGetsIt()
    {
        // "CRAWL DELAY — Preferred minimum number of hours between crawls." A server asking for more
        // than our default is asking politely and is obeyed.
        var options = new CrawlOptions { RevisitInterval = TimeSpan.FromHours(6) };
        var (frontier, host) = Seeded(options);

        var data = new MsspSubnegotiationParser()
            .Consume(MsspWire.Subnegotiation(("CRAWL DELAY", ["23"]))).Single();

        frontier.Record(Result(host, CrawlOutcome.MsspReceived, data));

        await Assert.That(frontier.Records.Single().NotBefore).IsEqualTo(Now + TimeSpan.FromHours(23));
        await Assert.That(frontier.Records.Single().CrawlDelayHours).IsEqualTo(23);
    }

    [Test]
    public async Task AServerAskingForAShorterCrawlDelayDoesNotGetVisitedMoreOften()
    {
        // It is a minimum a server asks for, not a permission it grants. Our own politeness setting is
        // not theirs to lower.
        var options = new CrawlOptions { RevisitInterval = TimeSpan.FromHours(24) };
        var (frontier, host) = Seeded(options);

        var data = new MsspSubnegotiationParser()
            .Consume(MsspWire.Subnegotiation(("CRAWL DELAY", ["1"]))).Single();

        frontier.Record(Result(host, CrawlOutcome.MsspReceived, data));

        await Assert.That(frontier.Records.Single().NotBefore).IsEqualTo(Now + TimeSpan.FromHours(24));
    }

    [Test]
    public async Task ADryRunDoesNotMoveTheSchedule()
    {
        // Checking a configuration must not silently postpone the crawl it was checking.
        var (frontier, host) = Seeded();

        frontier.Record(Result(host, CrawlOutcome.Skipped));
        var record = frontier.Records.Single();

        await Assert.That(record.Attempts).IsEqualTo(0);
        await Assert.That(record.LastAttempt).IsNull();
        await Assert.That(record.NotBefore).IsNull();
        await Assert.That(record.IsDue(Now)).IsTrue();
    }
}
