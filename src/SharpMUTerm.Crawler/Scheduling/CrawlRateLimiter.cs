using SharpMUTerm.Core.Telnet.Mssp;

namespace SharpMUTerm.Crawler.Scheduling;

/// <summary>
/// The two time-based limits on a crawl: a floor under the gap between any two connections, and a
/// floor under the gap between two connections to the same host.
/// <para>
/// Deliberately not a timer, a queue, or anything that sleeps. It answers one question —
/// <see cref="DelayBefore"/>: how long from now until this host may be dialled — and records one fact
/// — <see cref="RecordStart"/>: it just was. Waiting is the caller's job. That separation is what makes
/// the limit assertable: a test drives an injected <see cref="TimeProvider"/> and reads the answers
/// back, instead of sleeping for the interval and hoping the machine agreed.
/// </para>
/// <para>
/// The third limit, how many connections may be open at once, is not here. It is a semaphore in the
/// crawl loop, because it is a fact about connections in flight rather than about time, and folding
/// the two together would make neither testable.
/// </para>
/// </summary>
public sealed class CrawlRateLimiter(CrawlOptions options, TimeProvider time)
{
    private readonly Dictionary<MsspHost, DateTimeOffset> _lastPerHost = [];
    private readonly Lock _gate = new();

    private DateTimeOffset? _lastAny;

    /// <summary>
    /// How long from now before <paramref name="host"/> may be connected to: zero when it may be now,
    /// otherwise the longer of the two waits owed.
    /// </summary>
    public TimeSpan DelayBefore(MsspHost host)
    {
        lock (_gate)
        {
            return DelayLocked(host, time.GetUtcNow());
        }
    }

    private TimeSpan DelayLocked(MsspHost host, DateTimeOffset now)
    {
        var globalReady = _lastAny is { } lastAny ? lastAny + options.GlobalInterval : now;
        var hostReady = _lastPerHost.TryGetValue(host, out var lastHost)
            ? lastHost + options.PerHostInterval
            : now;

        var ready = globalReady > hostReady ? globalReady : hostReady;
        var wait = ready - now;
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    /// <summary>
    /// Stamps a connection as starting now, against both limits.
    /// <para>
    /// Called at the moment the connection is <em>started</em>, not when it finishes. Stamping on
    /// completion would let a burst of slow connections all start together and would make the effective
    /// rate depend on how fast the servers answered — the opposite of a rate limit.
    /// </para>
    /// </summary>
    public void RecordStart(MsspHost host)
    {
        lock (_gate)
        {
            RecordStartLocked(host, time.GetUtcNow());
        }
    }

    private void RecordStartLocked(MsspHost host, DateTimeOffset now)
    {
        _lastAny = now;
        _lastPerHost[host] = now;
    }

    /// <summary>
    /// Waits out <see cref="DelayBefore"/> and then stamps the start, re-checking after each wait.
    /// <para>
    /// The loop matters under concurrency: two workers can both be told to wait one second, and if both
    /// simply waited and started, the global interval would have been halved. Re-asking after the wait
    /// means the second one is told to wait again by however much the first consumed.
    /// </para>
    /// </summary>
    public async Task WaitForTurnAsync(MsspHost host, CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_gate)
            {
                var now = time.GetUtcNow();
                wait = DelayLocked(host, now);
                if (wait <= TimeSpan.Zero)
                {
                    RecordStartLocked(host, now);
                    return;
                }
            }

            await Task.Delay(wait, time, cancellationToken).ConfigureAwait(false);
        }
    }
}
