using SharpMUTerm.Core.Telnet.Mssp;

namespace SharpMUTerm.Crawler.Model;

/// <summary>How one attempt on one host ended.</summary>
public enum CrawlOutcome
{
    /// <summary>Never attempted.</summary>
    Unknown,

    /// <summary>Connected, negotiated, and the server sent MSSP.</summary>
    MsspReceived,

    /// <summary>
    /// Connected and negotiated, but the server never offered MSSP. Not a failure — a great many
    /// servers simply do not implement it — so it does not count toward retirement. It does earn a much
    /// longer wait before being asked again.
    /// </summary>
    NoMssp,

    /// <summary>The connection was refused, reset, or the host did not resolve.</summary>
    ConnectFailed,

    /// <summary>The connection was accepted but nothing arrived before the deadline.</summary>
    Timeout,

    /// <summary>The connection worked and something went wrong above it.</summary>
    Error,

    /// <summary>A dry run: the host was selected and deliberately not contacted.</summary>
    Skipped,
}

/// <summary>What one probe of one host produced.</summary>
public sealed record ProbeResult
{
    public required MsspHost Host { get; init; }

    public required CrawlOutcome Outcome { get; init; }

    /// <summary>When the attempt started, in UTC.</summary>
    public required DateTimeOffset ObservedAt { get; init; }

    /// <summary>How long the attempt took, from connect to close.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>The server's report, when there was one.</summary>
    public MsspData? Data { get; init; }

    /// <summary>A one-line description of what went wrong, when something did.</summary>
    public string? Error { get; init; }

    public bool Succeeded => Outcome == CrawlOutcome.MsspReceived;

    /// <summary>
    /// Whether this attempt counts toward a host's retirement. A server that answered without MSSP did
    /// nothing wrong, and a dry run did not happen; neither should be able to retire a host.
    /// </summary>
    public bool IsFailure => Outcome is CrawlOutcome.ConnectFailed or CrawlOutcome.Timeout or CrawlOutcome.Error;
}

/// <summary>
/// What the crawler remembers about one host between runs: enough to resume, to honour a revisit
/// interval, and to stop knocking on a door that never opens.
/// </summary>
public sealed class HostRecord
{
    public required MsspHost Host { get; init; }

    /// <summary>How many referral hops from a seed this host was first reached at.</summary>
    public int Depth { get; set; }

    /// <summary>The host that referred us here, or null for a seed.</summary>
    public MsspHost? DiscoveredFrom { get; set; }

    public DateTimeOffset FirstSeen { get; set; }

    public DateTimeOffset? LastAttempt { get; set; }

    public DateTimeOffset? LastSuccess { get; set; }

    public CrawlOutcome LastOutcome { get; set; } = CrawlOutcome.Unknown;

    public string? LastError { get; set; }

    /// <summary>The MUD's name as last reported, kept so a report can name a host it did not visit this run.</summary>
    public string? Name { get; set; }

    /// <summary>The <c>CRAWL DELAY</c> the server last asked for, in hours; null when it asked for nothing.</summary>
    public double? CrawlDelayHours { get; set; }

    public int ConsecutiveFailures { get; set; }

    public int Attempts { get; set; }

    /// <summary>
    /// The instant this host may next be attempted. One field, written by
    /// <see cref="CrawlFrontierPolicy"/>, holding the combined answer of the revisit interval, the
    /// server's own <c>CRAWL DELAY</c> and the failure backoff — so "is this due?" is one comparison and
    /// resume across runs needs nothing recomputed.
    /// </summary>
    public DateTimeOffset? NotBefore { get; set; }

    /// <summary>
    /// True when this host has failed too many times running and will not be attempted again. It stays
    /// in the store so the decision survives a restart and so a human can see it was made.
    /// </summary>
    public bool Retired { get; set; }

    public bool IsDue(DateTimeOffset now) => !Retired && (NotBefore is null || NotBefore <= now);
}
