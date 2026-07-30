using SharpMUTerm.Core.Telnet.Mssp;
using SharpMUTerm.Crawler.Model;
using SharpMUTerm.Crawler.Output;
using SharpMUTerm.Crawler.Scheduling;
using SharpMUTerm.Crawler.Tests.Support;

namespace SharpMUTerm.Crawler.Tests;

/// <summary>
/// The crawl loop, against a scripted probe. Time is virtual throughout — the loop really does wait on
/// its rate limiter, and here waiting moves the clock instead of the thread.
/// </summary>
public class CrawlLoopTests
{
    private static MsspHost Host(string name, int port = 4201) => MsspHost.Create(name, port)!;

    /// <summary>A configuration with the politeness intervals at zero, for tests that are not about them.</summary>
    private static CrawlOptions Fast(CrawlOptions? from = null) => (from ?? new CrawlOptions()) with
    {
        GlobalInterval = TimeSpan.Zero,
        PerHostInterval = TimeSpan.Zero,
        MaxConcurrency = 1,
    };

    [Test]
    public async Task ACrawlFollowsReferralsOutwardsFromItsSeed()
    {
        var time = new VirtualTimeProvider();
        var a = Host("a.example.org");
        var b = Host("b.example.org");
        var c = Host("c.example.org");

        var probe = new FakeProbe(time).Referring(a, b).Referring(b, c);
        var options = Fast();
        var frontier = new CrawlFrontier(options);
        frontier.AddSeed(a, time.GetUtcNow());

        var summary = await new MsspCrawler(options, probe, frontier, time).RunAsync();

        await Assert.That(probe.Visited).IsEquivalentTo(new[] { a, b, c });
        await Assert.That(summary.StopReason).IsEqualTo(CrawlStopReason.Exhausted);
        await Assert.That(frontier.Records.Count).IsEqualTo(3);
    }

    [Test]
    public async Task ACycleOfReferralsVisitsEachServerExactlyOnce()
    {
        // A ↔ B and both pointing at C, which points back at A. Every arrow is followed, no host is
        // dialled twice, and the run ends.
        var time = new VirtualTimeProvider();
        var a = Host("a.example.org");
        var b = Host("b.example.org");
        var c = Host("c.example.org");

        var probe = new FakeProbe(time)
            .Referring(a, b, c)
            .Referring(b, a, c)
            .Referring(c, a, b);

        var options = Fast();
        var frontier = new CrawlFrontier(options);
        frontier.AddSeed(a, time.GetUtcNow());

        var summary = await new MsspCrawler(options, probe, frontier, time).RunAsync();

        await Assert.That(probe.Visited.Count).IsEqualTo(3);
        await Assert.That(probe.Visited.Distinct().Count()).IsEqualTo(3);
        await Assert.That(summary.StopReason).IsEqualTo(CrawlStopReason.Exhausted);
        await Assert.That(summary.Verdicts[DiscoveryVerdict.AlreadyKnown]).IsGreaterThan(0);
    }

    [Test]
    public async Task TheHostCapStopsTheRunEvenWithReferralsLeftToFollow()
    {
        // Each server refers to two more, so the frontier grows faster than it is consumed and only the
        // cap can end this.
        var time = new VirtualTimeProvider();
        var probe = new FakeProbe(time);
        for (var i = 0; i < 200; i++)
        {
            probe.Referring(Host($"h{i}.example.org"), Host($"h{i * 2 + 1}.example.org"), Host($"h{i * 2 + 2}.example.org"));
        }

        var options = Fast() with { MaxHosts = 7, MaxDepth = 99 };
        var frontier = new CrawlFrontier(options);
        frontier.AddSeed(Host("h0.example.org"), time.GetUtcNow());

        var summary = await new MsspCrawler(options, probe, frontier, time).RunAsync();

        await Assert.That(summary.StopReason).IsEqualTo(CrawlStopReason.HostCap);
        await Assert.That(probe.Visited.Count).IsEqualTo(7);
        await Assert.That(summary.Contacted).IsEqualTo(7);

        // The frontier still knows about the ones it did not reach, so the next run picks them up.
        await Assert.That(frontier.Records.Count).IsGreaterThan(7);
    }

    [Test]
    public async Task TheHostCapIsNeverExceededEvenWhenSeveralWorkersRace()
    {
        var time = new VirtualTimeProvider();
        var probe = new FakeProbe(time);
        for (var i = 0; i < 200; i++)
        {
            probe.Referring(Host($"h{i}.example.org"), Host($"h{i * 2 + 1}.example.org"), Host($"h{i * 2 + 2}.example.org"));
        }

        var options = Fast() with { MaxHosts = 10, MaxDepth = 99, MaxConcurrency = 8 };
        var frontier = new CrawlFrontier(options);
        frontier.AddSeed(Host("h0.example.org"), time.GetUtcNow());

        await new MsspCrawler(options, probe, frontier, time).RunAsync();

        await Assert.That(probe.Visited.Count).IsEqualTo(10);
    }

    [Test]
    public async Task ACapDoesNotAbortAConnectionAlreadyInFlight()
    {
        // Found by watching a real run: the cap used to cancel the crawl's token, which killed the probes
        // already talking to a server. The data was thrown away, the server was left with a connection
        // dropped mid-handshake, and the host was recorded as never attempted — so the next run dialled
        // it again. A cap must stop the crawl starting anything new and nothing else.
        var time = new VirtualTimeProvider();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new FakeProbe(time) { Gate = gate };
        for (var i = 0; i < 50; i++)
        {
            probe.Referring(Host($"h{i}.example.org"), Host($"h{i * 2 + 1}.example.org"), Host($"h{i * 2 + 2}.example.org"));
        }

        var options = Fast() with { MaxHosts = 3, MaxDepth = 99, MaxConcurrency = 3 };
        var frontier = new CrawlFrontier(options);
        for (var i = 0; i < 3; i++)
        {
            frontier.AddSeed(Host($"h{i}.example.org"), time.GetUtcNow());
        }

        var run = new MsspCrawler(options, probe, frontier, time).RunAsync();

        // All three workers are inside a probe when the cap is reached.
        await Task.Delay(150);
        gate.SetResult();
        var summary = await run;

        await Assert.That(summary.StopReason).IsEqualTo(CrawlStopReason.HostCap);
        await Assert.That(probe.Visited.Count).IsEqualTo(3);

        // Every probe that started was allowed to finish and was recorded as a real observation.
        await Assert.That(summary.Results.Count).IsEqualTo(3);
        await Assert.That(summary.Results.All(r => r.Outcome == CrawlOutcome.MsspReceived)).IsTrue();
        await Assert.That(summary.Contacted).IsEqualTo(3);
    }

    [Test]
    public async Task TheDepthCapStopsAChainOfReferrals()
    {
        var time = new VirtualTimeProvider();
        var probe = new FakeProbe(time);
        for (var i = 0; i < 20; i++)
        {
            probe.Referring(Host($"h{i}.example.org"), Host($"h{i + 1}.example.org"));
        }

        var options = Fast() with { MaxDepth = 2, MaxHosts = 1000 };
        var frontier = new CrawlFrontier(options);
        frontier.AddSeed(Host("h0.example.org"), time.GetUtcNow());

        var summary = await new MsspCrawler(options, probe, frontier, time).RunAsync();

        // The seed plus two hops.
        await Assert.That(probe.Visited.Count).IsEqualTo(3);
        await Assert.That(summary.StopReason).IsEqualTo(CrawlStopReason.Exhausted);
        await Assert.That(summary.Verdicts[DiscoveryVerdict.TooDeep]).IsEqualTo(1);
    }

    [Test]
    public async Task TheTimeCapStopsTheRun()
    {
        // With a one-second global interval and a ten-second budget, the rate limiter alone runs the
        // clock out. Virtual time makes that instant and exact.
        var time = new VirtualTimeProvider();
        var probe = new FakeProbe(time);
        for (var i = 0; i < 500; i++)
        {
            probe.Referring(Host($"h{i}.example.org"), Host($"h{i * 2 + 1}.example.org"), Host($"h{i * 2 + 2}.example.org"));
        }

        var options = new CrawlOptions
        {
            GlobalInterval = TimeSpan.FromSeconds(1),
            PerHostInterval = TimeSpan.Zero,
            MaxConcurrency = 1,
            MaxDuration = TimeSpan.FromSeconds(10),
            MaxHosts = 10_000,
            MaxDepth = 99,
        };

        var frontier = new CrawlFrontier(options);
        frontier.AddSeed(Host("h0.example.org"), time.GetUtcNow());

        var started = time.GetUtcNow();
        var summary = await new MsspCrawler(options, probe, frontier, time).RunAsync();

        await Assert.That(summary.StopReason).IsEqualTo(CrawlStopReason.TimeCap);
        await Assert.That(time.GetUtcNow() - started).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(10));

        // Roughly one host per second of budget, which is the rate limiter doing its job inside the loop
        // rather than the loop merely being told about it.
        await Assert.That(probe.Visited.Count).IsLessThanOrEqualTo(12);
    }

    [Test]
    public async Task TheConcurrencyCapIsRespected()
    {
        var time = new VirtualTimeProvider();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new FakeProbe(time) { Gate = gate };

        var seedOptions = Fast() with { MaxConcurrency = 3, MaxHosts = 100 };
        var frontier = new CrawlFrontier(seedOptions);
        for (var i = 0; i < 20; i++)
        {
            frontier.AddSeed(Host($"h{i}.example.org"), time.GetUtcNow());
        }

        var run = new MsspCrawler(seedOptions, probe, frontier, time).RunAsync();

        // Let the workers pile up against the gate, then release them.
        await Task.Delay(150);
        var peakWhileBlocked = probe.PeakConcurrency;
        gate.SetResult();
        await run;

        await Assert.That(peakWhileBlocked).IsEqualTo(3);
        await Assert.That(probe.PeakConcurrency).IsLessThanOrEqualTo(3);
        await Assert.That(probe.Visited.Count).IsEqualTo(20);
    }

    [Test]
    public async Task ARunResumesWhereThePreviousOneStopped()
    {
        var time = new VirtualTimeProvider();
        var options = Fast() with { MaxHosts = 2, MaxDepth = 99 };

        var probe = new FakeProbe(time);
        for (var i = 0; i < 20; i++)
        {
            probe.Referring(Host($"h{i}.example.org"), Host($"h{i + 1}.example.org"));
        }

        // First run: stopped by the host cap after two.
        var first = new CrawlFrontier(options);
        first.AddSeed(Host("h0.example.org"), time.GetUtcNow());
        IReadOnlyCollection<HostRecord> saved = [];
        var firstSummary = await new MsspCrawler(options, probe, first, time, persist: records => saved = records)
            .RunAsync();

        await Assert.That(firstSummary.StopReason).IsEqualTo(CrawlStopReason.HostCap);
        await Assert.That(probe.Visited).IsEquivalentTo(new[] { Host("h0.example.org"), Host("h1.example.org") });

        // Second run, from the saved state and with no seeds at all: it must continue rather than start
        // over, and must not re-dial the two it has just visited.
        var second = new CrawlFrontier(options, saved);
        var resumed = new FakeProbe(time);
        for (var i = 0; i < 20; i++)
        {
            resumed.Referring(Host($"h{i}.example.org"), Host($"h{i + 1}.example.org"));
        }

        await new MsspCrawler(options, resumed, second, time).RunAsync();

        await Assert.That(resumed.Visited).IsEquivalentTo(new[] { Host("h2.example.org"), Host("h3.example.org") });
    }

    [Test]
    public async Task ARunWithNothingDueContactsNobody()
    {
        // The whole point of the revisit interval: a second run started straight after the first must be
        // a no-op, not a second pass over the same servers.
        var time = new VirtualTimeProvider();
        var options = Fast() with { MaxHosts = 100 };

        var frontier = new CrawlFrontier(options);
        frontier.AddSeed(Host("a.example.org"), time.GetUtcNow());
        var probe = new FakeProbe(time).Referring(Host("a.example.org"));

        IReadOnlyCollection<HostRecord> saved = [];
        await new MsspCrawler(options, probe, frontier, time, persist: records => saved = records).RunAsync();
        await Assert.That(probe.Visited.Count).IsEqualTo(1);

        var again = new FakeProbe(time);
        var summary = await new MsspCrawler(options, again, new CrawlFrontier(options, saved), time).RunAsync();

        await Assert.That(again.Visited).IsEmpty();
        await Assert.That(summary.Contacted).IsEqualTo(0);

        // …and once the interval has passed, it is due again.
        time.Advance(TimeSpan.FromHours(25));
        var later = new FakeProbe(time);
        await new MsspCrawler(options, later, new CrawlFrontier(options, saved), time).RunAsync();
        await Assert.That(later.Visited.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ADryRunContactsNobodyAndLeavesTheScheduleAlone()
    {
        var time = new VirtualTimeProvider();
        var options = Fast() with { DryRun = true };
        var frontier = new CrawlFrontier(options);
        frontier.AddSeed(Host("a.example.org"), time.GetUtcNow());

        var probe = new FakeProbe(time);
        var summary = await new MsspCrawler(options, probe, frontier, time).RunAsync();

        await Assert.That(probe.Visited).IsEmpty();
        await Assert.That(summary.Contacted).IsEqualTo(0);
        await Assert.That(frontier.Records.Single().IsDue(time.GetUtcNow())).IsTrue();
    }

    [Test]
    public async Task AFailingHostBacksOffAndIsNotRetriedWithinTheSameRun()
    {
        var time = new VirtualTimeProvider();
        var options = Fast() with { MaxHosts = 100 };
        var host = Host("dead.example.org");

        var frontier = new CrawlFrontier(options);
        frontier.AddSeed(host, time.GetUtcNow());
        var probe = new FakeProbe(time).Answering(host, CrawlOutcome.ConnectFailed, "ConnectionRefused");

        await new MsspCrawler(options, probe, frontier, time).RunAsync();

        await Assert.That(probe.Visited.Count).IsEqualTo(1);
        var record = frontier.Records.Single();
        await Assert.That(record.ConsecutiveFailures).IsEqualTo(1);
        await Assert.That(record.LastError).IsEqualTo("ConnectionRefused");
        await Assert.That(record.NotBefore).IsNotNull();
    }

    [Test]
    public async Task AProbeThatThrowsIsRecordedRatherThanTakingTheRunDown()
    {
        var time = new VirtualTimeProvider();
        var options = Fast();
        var frontier = new CrawlFrontier(options);
        frontier.AddSeed(Host("a.example.org"), time.GetUtcNow());

        var summary = await new MsspCrawler(options, new ThrowingProbe(), frontier, time).RunAsync();

        await Assert.That(summary.Results.Single().Outcome).IsEqualTo(CrawlOutcome.Error);
        await Assert.That(summary.StopReason).IsEqualTo(CrawlStopReason.Exhausted);
    }

    private sealed class ThrowingProbe : SharpMUTerm.Crawler.Probing.IMsspProbe
    {
        public Task<ProbeResult> ProbeAsync(MsspHost host, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("a bug in the probe");
    }
}
