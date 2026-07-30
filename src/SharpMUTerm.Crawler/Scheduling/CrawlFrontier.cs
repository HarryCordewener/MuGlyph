using SharpMUTerm.Core.Telnet.Mssp;
using SharpMUTerm.Crawler.Model;

namespace SharpMUTerm.Crawler.Scheduling;

/// <summary>Why a discovered host was not added to the frontier. Counted so a run can explain itself.</summary>
public enum DiscoveryVerdict
{
    /// <summary>Added; it had not been seen before.</summary>
    Added,

    /// <summary>Already known — from this run, an earlier run, or another referrer. Nothing to do.</summary>
    AlreadyKnown,

    /// <summary>The referral pointed at the server that sent it.</summary>
    SelfReferral,

    /// <summary>Beyond <see cref="CrawlOptions.MaxDepth"/> hops from a seed.</summary>
    TooDeep,

    /// <summary>A loopback, private, link-local or multicast address.</summary>
    NotRoutable,

    /// <summary>Referral following is switched off for this run.</summary>
    ReferralsDisabled,
}

/// <summary>
/// The set of hosts a run knows about and the order it works through them.
/// <para>
/// <b>This is where referral cycles stop.</b> They are not an edge case to be handled if they arise:
/// with MSSP they are certain, because a referral list is a mutual courtesy — A lists B and B lists
/// A, and any real referral graph is full of triangles besides. The frontier is keyed by
/// <see cref="MsspHost"/>, whose equality is over the <em>normalised</em> host and port, and a host is
/// added at most once ever. So B's referral back to A finds A already known and stops there; a
/// three-cycle stops on the third hop; and a chain of hosts that each refer to the next by a different
/// spelling of the same name (<c>MUD.Example.org</c>, <c>mud.example.org.</c>) collapses to one entry
/// rather than looping through spellings. Depth is recorded but is not part of identity, so a host
/// reached again by a shorter path does not become a second host.
/// </para>
/// <para>
/// Deduplication alone bounds the crawl only if the graph is finite, which nothing guarantees — a
/// server can generate referrals. <see cref="CrawlOptions.MaxDepth"/> and
/// <see cref="CrawlOptions.MaxHosts"/> are the guarantees.
/// </para>
/// </summary>
public sealed class CrawlFrontier
{
    private readonly CrawlOptions _options;
    private readonly Dictionary<MsspHost, HostRecord> _records;
    private readonly HashSet<MsspHost> _inFlight = [];
    private readonly Lock _gate = new();

    public CrawlFrontier(CrawlOptions options, IEnumerable<HostRecord>? resumed = null)
    {
        _options = options;
        _records = (resumed ?? []).ToDictionary(record => record.Host);
    }

    /// <summary>Every host known to this run, including ones carried over from a previous one.</summary>
    public IReadOnlyCollection<HostRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return _records.Values.ToList();
            }
        }
    }

    /// <summary>How many hosts were discovered by a referral during this run.</summary>
    public int Discovered { get; private set; }

    /// <summary>Referrals that were rejected, by reason. The interesting half of a crawl's shape.</summary>
    public IReadOnlyDictionary<DiscoveryVerdict, int> Verdicts
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<DiscoveryVerdict, int>(_verdicts);
            }
        }
    }

    private readonly Dictionary<DiscoveryVerdict, int> _verdicts = [];

    /// <summary>
    /// Adds a seed. Seeds are depth 0 and, unlike referrals, are never rejected for depth or scope: a
    /// human naming a host explicitly — including <c>127.0.0.1</c> against their own test server — has
    /// said what they mean, and this tool's job is not to second-guess its own operator. What it must
    /// not do is let a <em>stranger</em> aim it, which is what the referral path checks.
    /// </summary>
    public DiscoveryVerdict AddSeed(MsspHost host, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_records.ContainsKey(host))
            {
                return Count(DiscoveryVerdict.AlreadyKnown);
            }

            _records[host] = new HostRecord { Host = host, Depth = 0, FirstSeen = now };
            return Count(DiscoveryVerdict.Added);
        }
    }

    /// <summary>
    /// Considers a host named by <paramref name="referrer"/>'s <c>REFERRAL</c> list.
    /// </summary>
    public DiscoveryVerdict Discover(MsspHost host, MsspHost referrer, int referrerDepth, DateTimeOffset now)
    {
        if (!_options.FollowReferrals)
        {
            lock (_gate)
            {
                return Count(DiscoveryVerdict.ReferralsDisabled);
            }
        }

        if (host == referrer)
        {
            // A server listing itself. Harmless, common — a referral list is often just "everyone I know
            // of, including me" — and nothing to act on.
            lock (_gate)
            {
                return Count(DiscoveryVerdict.SelfReferral);
            }
        }

        if (!_options.FollowPrivateAddresses && !host.IsCrawlable)
        {
            lock (_gate)
            {
                return Count(DiscoveryVerdict.NotRoutable);
            }
        }

        var depth = referrerDepth + 1;
        if (depth > _options.MaxDepth)
        {
            lock (_gate)
            {
                return Count(DiscoveryVerdict.TooDeep);
            }
        }

        lock (_gate)
        {
            if (_records.TryGetValue(host, out var existing))
            {
                // Known already — the cycle case, and much the most common outcome once a crawl is under
                // way. Reaching it by a shorter path is still worth recording, because depth is what the
                // cap is measured against and a host first seen at the limit would otherwise never have
                // its own referrals followed.
                if (depth < existing.Depth)
                {
                    existing.Depth = depth;
                }

                return Count(DiscoveryVerdict.AlreadyKnown);
            }

            _records[host] = new HostRecord
            {
                Host = host,
                Depth = depth,
                DiscoveredFrom = referrer,
                FirstSeen = now,
            };

            Discovered++;
            return Count(DiscoveryVerdict.Added);
        }
    }

    /// <summary>
    /// Takes the next host that may be attempted now, or null when none is due. Shallowest first, then
    /// oldest-known first: a breadth-first walk, so a run cut short by its caps has covered the network
    /// around its seeds rather than one arbitrary branch of it.
    /// </summary>
    public HostRecord? TakeNext(DateTimeOffset now)
    {
        lock (_gate)
        {
            var next = _records.Values
                .Where(record => !_inFlight.Contains(record.Host) && record.IsDue(now))
                .OrderBy(record => record.Depth)
                .ThenBy(record => record.FirstSeen)
                .FirstOrDefault();

            if (next is not null)
            {
                _inFlight.Add(next.Host);
            }

            return next;
        }
    }

    /// <summary>Whether anything at all is still waiting to be attempted.</summary>
    public bool HasWork(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _records.Values.Any(record => !_inFlight.Contains(record.Host) && record.IsDue(now));
        }
    }

    /// <summary>
    /// Writes an attempt's outcome into the host's record and decides when — or whether — it may be
    /// attempted again. This is the single place the revisit interval, the server's own
    /// <c>CRAWL DELAY</c>, the failure backoff and retirement all meet, so those four cannot disagree.
    /// </summary>
    public void Record(ProbeResult result)
    {
        lock (_gate)
        {
            _inFlight.Remove(result.Host);
            if (!_records.TryGetValue(result.Host, out var record))
            {
                return;
            }

            record.Attempts++;
            record.LastAttempt = result.ObservedAt;
            record.LastOutcome = result.Outcome;
            record.LastError = result.Error;

            if (result.Outcome == CrawlOutcome.Skipped)
            {
                // A dry run touched nothing, so it may not move the schedule either — otherwise checking
                // a configuration would silently postpone the real crawl it was checking.
                record.Attempts--;
                record.LastAttempt = null;
                record.LastOutcome = CrawlOutcome.Skipped;
                return;
            }

            if (result.IsFailure)
            {
                record.ConsecutiveFailures++;
                if (record.ConsecutiveFailures >= _options.RetireAfterFailures)
                {
                    record.Retired = true;
                    record.NotBefore = null;
                    return;
                }

                record.NotBefore = result.ObservedAt + _options.ResolveBackoff(record.ConsecutiveFailures);
                return;
            }

            record.ConsecutiveFailures = 0;

            if (result.Outcome == CrawlOutcome.NoMssp)
            {
                record.NotBefore = result.ObservedAt + _options.NoMsspRevisitInterval;
                return;
            }

            record.LastSuccess = result.ObservedAt;
            record.Name = result.Data?.Name ?? record.Name;
            record.CrawlDelayHours = result.Data?.CrawlDelay?.TotalHours ?? record.CrawlDelayHours;
            record.NotBefore = result.ObservedAt + _options.ResolveRevisit(result.Data?.CrawlDelay);
        }
    }

    private DiscoveryVerdict Count(DiscoveryVerdict verdict)
    {
        _verdicts[verdict] = _verdicts.GetValueOrDefault(verdict) + 1;
        return verdict;
    }
}
