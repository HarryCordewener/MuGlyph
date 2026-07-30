using SharpMUTerm.Core.Telnet.Mssp;
using SharpMUTerm.Crawler.Model;
using SharpMUTerm.Crawler.Probing;

namespace SharpMUTerm.Crawler.Tests.Support;

/// <summary>
/// A probe that answers from a script instead of a socket, so the crawl loop can be tested without a
/// network. Records every host it was asked about, in order, and how many were in flight at once.
/// </summary>
internal sealed class FakeProbe(TimeProvider time) : IMsspProbe
{
    private readonly Lock _gate = new();
    private readonly List<MsspHost> _visited = [];
    private readonly Dictionary<MsspHost, Func<MsspHost, ProbeResult>> _answers = [];

    private int _inFlight;

    /// <summary>Every host probed, in the order the loop reached them.</summary>
    public IReadOnlyList<MsspHost> Visited
    {
        get
        {
            lock (_gate)
            {
                return [.. _visited];
            }
        }
    }

    /// <summary>The most connections that were open at the same moment.</summary>
    public int PeakConcurrency { get; private set; }

    /// <summary>What an unscripted host answers with. Defaults to a server that has no MSSP.</summary>
    public CrawlOutcome DefaultOutcome { get; set; } = CrawlOutcome.NoMssp;

    /// <summary>Held open until released, so a test can observe several probes in flight at once.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Answers <paramref name="host"/> with a report referring to <paramref name="referrals"/>.</summary>
    public FakeProbe Referring(MsspHost host, params MsspHost[] referrals)
    {
        var data = new MsspSubnegotiationParser()
            .Consume(MsspWire.Subnegotiation(
                ("NAME", [$"Server at {host.Host}"]),
                ("REFERRAL", referrals.Select(r => r.ToReferralString()).ToArray())))
            .Single();

        lock (_gate)
        {
            _answers[host] = probed => new ProbeResult
            {
                Host = probed,
                Outcome = CrawlOutcome.MsspReceived,
                ObservedAt = time.GetUtcNow(),
                Data = data,
            };
        }

        return this;
    }

    /// <summary>Answers <paramref name="host"/> with a specific outcome and no data.</summary>
    public FakeProbe Answering(MsspHost host, CrawlOutcome outcome, string? error = null)
    {
        lock (_gate)
        {
            _answers[host] = probed => new ProbeResult
            {
                Host = probed,
                Outcome = outcome,
                ObservedAt = time.GetUtcNow(),
                Error = error,
            };
        }

        return this;
    }

    public async Task<ProbeResult> ProbeAsync(MsspHost host, CancellationToken cancellationToken)
    {
        Func<MsspHost, ProbeResult>? answer;
        lock (_gate)
        {
            _visited.Add(host);
            _inFlight++;
            PeakConcurrency = Math.Max(PeakConcurrency, _inFlight);
            _answers.TryGetValue(host, out answer);
        }

        try
        {
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }

            return answer?.Invoke(host) ?? new ProbeResult
            {
                Host = host,
                Outcome = DefaultOutcome,
                ObservedAt = time.GetUtcNow(),
            };
        }
        finally
        {
            lock (_gate)
            {
                _inFlight--;
            }
        }
    }
}
