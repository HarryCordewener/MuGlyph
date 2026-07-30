using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpMUTerm.Core.Telnet.Mssp;
using SharpMUTerm.Crawler.Model;
using SharpMUTerm.Crawler.Output;
using SharpMUTerm.Crawler.Probing;
using SharpMUTerm.Crawler.Scheduling;

namespace SharpMUTerm.Crawler;

/// <summary>
/// The crawl: take the next host that is due, wait for the rate limiter's permission, probe it,
/// record what came back, add whatever it referred us to, repeat until a cap says stop.
/// <para>
/// Three limits end a run and all three are checked in one place, before any host is claimed, so none
/// of them can be missed by a worker already inside the loop: the host cap, the time cap, and the
/// operator's interrupt. There is no arrangement of referrals that gets past them, which is the point
/// — a referral graph has no natural end, and an unattended tool that only stops when the network does
/// is not something to point at other people's servers. A fourth, <see cref="CrawlOptions.MaxDepth"/>,
/// is enforced where referrals are accepted rather than here.
/// </para>
/// </summary>
public sealed class MsspCrawler(
    CrawlOptions options,
    IMsspProbe probe,
    CrawlFrontier frontier,
    TimeProvider? time = null,
    ILogger? logger = null,
    ObservationLog? observations = null,
    Action<IReadOnlyCollection<HostRecord>>? persist = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly CrawlRateLimiter _limiter = new(options, time ?? TimeProvider.System);
    private readonly List<ProbeResult> _results = [];
    private readonly Lock _gate = new();

    private int _contacted;
    private int _active;

    /// <summary>
    /// Set once a cap has been reached: no further host is claimed, and every idle worker returns.
    /// <para>
    /// A flag rather than a cancellation, because a cap must stop the crawl from starting anything new
    /// without aborting a connection that is already open. Cancelling in-flight probes would throw away
    /// a conversation with a stranger's server that we have already paid for — the data is lost, the
    /// server is left with a connection dropped mid-handshake, and the host is recorded as unattempted
    /// so the next run dials it again. Cancellation stays what the operator's interrupt does.
    /// </para>
    /// </summary>
    private CrawlStopReason? _stopped;

    /// <summary>
    /// Completed whenever the amount of work may have changed — a probe finished, referrals were added.
    /// It is how a worker with nothing to do waits for a peer's probe to produce something instead of
    /// polling, and it is replaced rather than reset so a worker that captured the old one is always
    /// woken exactly once.
    /// </summary>
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<CrawlSummary> RunAsync(CancellationToken cancellationToken = default)
    {
        options.Validate();

        var startedAt = _time.GetUtcNow();
        var deadline = startedAt + options.MaxDuration;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var workers = Enumerable
            .Range(0, options.MaxConcurrency)
            .Select(_ => Task.Run(() => WorkAsync(deadline, stop), CancellationToken.None))
            .ToArray();

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The operator interrupted: a worker parked waiting for its peers is woken by the token
            // rather than by Signal. Caps do not come through here — they stop the crawl without
            // cancelling anything, so that probes already talking to a server are allowed to finish.
        }

        var hosts = frontier.Records.ToList();
        persist?.Invoke(hosts);

        return new CrawlSummary
        {
            StartedAt = startedAt,
            FinishedAt = _time.GetUtcNow(),

            // An interrupt outranks a cap: if both happened, what a reader needs to know is that a human
            // stopped it. With neither, the crawl simply ran out of hosts that were due.
            StopReason = cancellationToken.IsCancellationRequested
                ? CrawlStopReason.Cancelled
                : _stopped ?? CrawlStopReason.Exhausted,
            Results = _results.ToList(),
            Hosts = hosts,
            Verdicts = frontier.Verdicts,
        };
    }

    private async Task WorkAsync(DateTimeOffset deadline, CancellationTokenSource stop)
    {
        while (!stop.IsCancellationRequested)
        {
            HostRecord? next;
            Task wake;

            lock (_gate)
            {
                if (_stopped is not null)
                {
                    return;
                }

                if (_contacted >= options.MaxHosts)
                {
                    Halt(CrawlStopReason.HostCap);
                    return;
                }

                if (_time.GetUtcNow() >= deadline)
                {
                    Halt(CrawlStopReason.TimeCap);
                    return;
                }

                next = frontier.TakeNext(_time.GetUtcNow());
                if (next is null)
                {
                    // Nothing to take. That only means the crawl is over if nobody else is mid-probe:
                    // the one thing that can still create work is a referral list not yet received. So a
                    // worker with nothing to do waits for a peer to finish rather than exiting — an
                    // early exit would quietly collapse a four-way crawl to one worker the moment the
                    // frontier ran thin, which with a single seed is immediately.
                    if (_active == 0)
                    {
                        return;
                    }

                    // Captured inside the lock, with _active, so a probe that completes between the two
                    // cannot signal the old waiter and leave this one waiting on a task nobody will
                    // complete.
                    wake = _wake.Task;
                }
                else
                {
                    // Counted at the moment a host is claimed, not when its probe returns, so the cap is
                    // a cap on hosts contacted rather than on hosts finished — otherwise MaxConcurrency
                    // more connections than the cap allows would already be open by the time it tripped.
                    _contacted++;
                    _active++;
                    wake = Task.CompletedTask;
                }
            }

            if (next is null)
            {
                await wake.WaitAsync(stop.Token).ConfigureAwait(false);
                continue;
            }

            try
            {
                await ProbeAsync(next, stop).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    _active--;
                    Signal();
                }
            }
        }
    }

    /// <summary>Wakes every idle worker. Must be called with <see cref="_gate"/> held.</summary>
    private void Signal()
    {
        var previous = _wake;
        _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    private async Task ProbeAsync(HostRecord record, CancellationTokenSource stop)
    {
        if (options.DryRun)
        {
            Record(new ProbeResult
            {
                Host = record.Host,
                Outcome = CrawlOutcome.Skipped,
                ObservedAt = _time.GetUtcNow(),
            });

            _logger.LogInformation("Would crawl {Host} (depth {Depth}).", record.Host, record.Depth);
            return;
        }

        try
        {
            await _limiter.WaitForTurnAsync(record.Host, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Interrupted while waiting its turn. The host was claimed and never contacted, so it is
            // released with no attempt recorded — the next run finds it exactly as it was.
            frontier.Record(new ProbeResult
            {
                Host = record.Host,
                Outcome = CrawlOutcome.Skipped,
                ObservedAt = _time.GetUtcNow(),
            });

            return;
        }

        ProbeResult result;
        try
        {
            result = await probe.ProbeAsync(record.Host, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            frontier.Record(new ProbeResult
            {
                Host = record.Host,
                Outcome = CrawlOutcome.Skipped,
                ObservedAt = _time.GetUtcNow(),
            });

            return;
        }
        catch (Exception ex)
        {
            // A probe that throws is a bug here, not a fault of the server's; it is recorded as an
            // error against the host rather than being allowed to take the worker down with it.
            _logger.LogError(ex, "Probing {Host} threw.", record.Host);
            result = new ProbeResult
            {
                Host = record.Host,
                Outcome = CrawlOutcome.Error,
                ObservedAt = _time.GetUtcNow(),
                Error = ex.GetType().Name,
            };
        }

        Record(result);

        _logger.LogInformation(
            "{Host}: {Outcome}{Detail}.",
            record.Host,
            result.Outcome,
            result.Data is { } report
                ? $" — {report.Name ?? "unnamed"}, {report.Count} variables, {report.Referrals.Count} referrals"
                : result.Error is { } why ? $" ({why})" : string.Empty);

        if (result.Data is { } data)
        {
            Follow(data.Referrals, record);
        }

        // Persisted per observation rather than at the end, and after the referrals have been added, so
        // a crawl that is killed remembers both what it did and what it learned. The next run must not
        // re-dial everyone it just visited — which is the one thing the revisit interval exists to stop.
        persist?.Invoke(frontier.Records);
    }

    private void Follow(IReadOnlyList<MsspHost> referrals, HostRecord referrer)
    {
        if (referrals.Count == 0)
        {
            return;
        }

        var now = _time.GetUtcNow();
        var added = 0;
        foreach (var referral in referrals)
        {
            if (frontier.Discover(referral, referrer.Host, referrer.Depth, now) == DiscoveryVerdict.Added)
            {
                added++;
            }
        }

        _logger.LogInformation(
            "{Host} referred {Total} host(s); {Added} new.", referrer.Host, referrals.Count, added);
    }

    private void Record(ProbeResult result)
    {
        frontier.Record(result);
        observations?.Append(result);

        lock (_gate)
        {
            _results.Add(result);
        }
    }

    /// <summary>
    /// Ends the crawl. Must be called with <see cref="_gate"/> held. Idempotent, because every worker
    /// reaches the same cap at about the same moment and only the first of them has anything to say.
    /// </summary>
    private void Halt(CrawlStopReason reason)
    {
        if (_stopped is not null)
        {
            return;
        }

        _stopped = reason;
        _logger.LogInformation("Stopping: {Reason}.", reason);

        // Wakes anyone parked waiting for a peer's probe; they re-check _stopped and return. Nothing is
        // cancelled: the probes still running finish and are recorded.
        Signal();
    }
}
