using SharpMUTerm.Core.Telnet.Mssp;
using SharpMUTerm.Crawler.Scheduling;
using SharpMUTerm.Crawler.Tests.Support;

namespace SharpMUTerm.Crawler.Tests;

/// <summary>
/// The rate limiter, driven by a clock the test moves by hand. Nothing here sleeps: a limiter tested
/// by waiting for its own interval proves only that the machine was not too busy.
/// </summary>
public class RateLimitTests
{
    private static MsspHost Host(string name, int port = 4201) => MsspHost.Create(name, port)!;

    private static readonly CrawlOptions Options = new()
    {
        GlobalInterval = TimeSpan.FromSeconds(2),
        PerHostInterval = TimeSpan.FromMinutes(5),
    };

    [Test]
    public async Task TheFirstConnectionIsAllowedImmediately()
    {
        var limiter = new CrawlRateLimiter(Options, new ManualTimeProvider());

        await Assert.That(limiter.DelayBefore(Host("a.example.org"))).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task TheGlobalIntervalHoldsBackTheNextConnectionToADifferentHost()
    {
        var time = new ManualTimeProvider();
        var limiter = new CrawlRateLimiter(Options, time);

        limiter.RecordStart(Host("a.example.org"));

        // A different host, so the per-host limit has nothing to say; the global one does.
        await Assert.That(limiter.DelayBefore(Host("b.example.org"))).IsEqualTo(TimeSpan.FromSeconds(2));

        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(limiter.DelayBefore(Host("b.example.org"))).IsEqualTo(TimeSpan.FromSeconds(1));

        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(limiter.DelayBefore(Host("b.example.org"))).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task TheSameHostWaitsTheLongerPerHostInterval()
    {
        var time = new ManualTimeProvider();
        var limiter = new CrawlRateLimiter(Options, time);

        var host = Host("a.example.org");
        limiter.RecordStart(host);

        time.Advance(TimeSpan.FromSeconds(10));

        // The global interval is long since satisfied; the per-host one is not, and the longer of the
        // two is what is owed.
        await Assert.That(limiter.DelayBefore(Host("b.example.org"))).IsEqualTo(TimeSpan.Zero);
        await Assert.That(limiter.DelayBefore(host)).IsEqualTo(TimeSpan.FromSeconds(290));

        time.Advance(TimeSpan.FromSeconds(290));
        await Assert.That(limiter.DelayBefore(host)).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task PortsOnOneMachineAreSeparateHostsForRateLimitingButTheGlobalLimitStillApplies()
    {
        // Two ports on one server are two entries; the per-host limit is keyed on both. The global limit
        // is what stops a server with six advertised ports being dialled six times in a second.
        var time = new ManualTimeProvider();
        var limiter = new CrawlRateLimiter(Options, time);

        limiter.RecordStart(Host("a.example.org", 4201));
        await Assert.That(limiter.DelayBefore(Host("a.example.org", 4202))).IsEqualTo(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task AStreamOfConnectionsIsSpacedByTheGlobalInterval()
    {
        // The property that matters: over a run, connections start no closer together than the interval.
        var time = new ManualTimeProvider();
        var limiter = new CrawlRateLimiter(Options, time);
        var starts = new List<DateTimeOffset>();

        for (var i = 0; i < 5; i++)
        {
            var host = Host($"host{i}.example.org");
            var wait = limiter.DelayBefore(host);
            time.Advance(wait);
            starts.Add(time.GetUtcNow());
            limiter.RecordStart(host);
        }

        var gaps = starts.Zip(starts.Skip(1), (first, second) => second - first).ToList();
        await Assert.That(gaps).IsNotEmpty();
        foreach (var gap in gaps)
        {
            await Assert.That(gap).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(2));
        }
    }

    [Test]
    public async Task WaitingForATurnStampsTheStartSoTheNextCallerIsHeldBack()
    {
        var time = new ManualTimeProvider();
        var limiter = new CrawlRateLimiter(Options, time);

        // Nothing to wait for, so this completes without the clock moving at all.
        await limiter.WaitForTurnAsync(Host("a.example.org"), CancellationToken.None);

        await Assert.That(limiter.DelayBefore(Host("b.example.org"))).IsEqualTo(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task AZeroIntervalMeansNoWaitingAtAll()
    {
        // The configuration a test harness uses, and the one a careless operator might. It must work
        // rather than divide by something.
        var limiter = new CrawlRateLimiter(
            new CrawlOptions { GlobalInterval = TimeSpan.Zero, PerHostInterval = TimeSpan.Zero },
            new ManualTimeProvider());

        var host = Host("a.example.org");
        limiter.RecordStart(host);

        await Assert.That(limiter.DelayBefore(host)).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task TheDefaultsAreConservative()
    {
        // A pin on the politeness settings themselves. These are the numbers that reach other people's
        // servers, and lowering one should be a deliberate act with a test to change.
        var defaults = new CrawlOptions();

        await Assert.That(defaults.MaxConcurrency).IsEqualTo(4);
        await Assert.That(defaults.GlobalInterval).IsEqualTo(TimeSpan.FromSeconds(1));
        await Assert.That(defaults.PerHostInterval).IsEqualTo(TimeSpan.FromMinutes(5));
        await Assert.That(defaults.RevisitInterval).IsEqualTo(TimeSpan.FromHours(24));
        await Assert.That(defaults.NoMsspRevisitInterval).IsEqualTo(TimeSpan.FromDays(7));
        await Assert.That(defaults.MaxHosts).IsEqualTo(500);
        await Assert.That(defaults.MaxDuration).IsEqualTo(TimeSpan.FromHours(1));
        await Assert.That(defaults.MaxDepth).IsEqualTo(4);
        await Assert.That(defaults.FollowPrivateAddresses).IsFalse();
    }
}
