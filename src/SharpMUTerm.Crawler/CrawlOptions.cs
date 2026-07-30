using SharpMUTerm.Core.Telnet.Mssp;

namespace SharpMUTerm.Crawler;

/// <summary>
/// Everything a run is allowed to do, with defaults chosen to be conservative rather than fast.
/// <para>
/// This tool connects to strangers' game servers. MSSP exists so that it may — <c>REFERRAL</c> is
/// the specification's own discovery mechanism and the document says outright that "adding referrals
/// is important to make MSSP decentralized" — but a documented invitation is not a licence to be
/// expensive. Every default here errs toward the operator on the other end: fewer connections at
/// once, longer between them, a shorter leash on the whole run, and a revisit interval measured in
/// days rather than minutes.
/// </para>
/// </summary>
public sealed record CrawlOptions
{
    /// <summary>Where state, observations and the report are written. Never the user's config directory.</summary>
    public string OutputDirectory { get; init; } = "mssp-crawl";

    /// <summary>Hosts the run starts from, before anything is loaded from a previous run's state.</summary>
    public IReadOnlyList<MsspHost> Seeds { get; init; } = [];

    // ---- Politeness ----

    /// <summary>
    /// How many servers may be connected to at once. Four is enough to make progress and small enough
    /// that a crawl is never the reason a shared uplink is busy.
    /// </summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>
    /// The minimum gap between the starts of any two connections, anywhere. One per second is a rate no
    /// server can notice and no network can object to.
    /// </summary>
    public TimeSpan GlobalInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The minimum gap between two connections to the <em>same</em> host, within one run. This is not
    /// the revisit interval — it is the floor under a retry, so a host that fails and is reconsidered
    /// cannot be dialled twice in a row.
    /// </summary>
    public TimeSpan PerHostInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long to wait for the TCP connection itself.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long to hold a connection open waiting for MSSP once it is established. A server that means
    /// to send MSSP sends it in the opening handshake; this is generous enough for a slow link and
    /// short enough that a silent server costs almost nothing.
    /// </summary>
    public TimeSpan MsspTimeout { get; init; } = TimeSpan.FromSeconds(20);

    // ---- Revisiting ----

    /// <summary>
    /// How long before a server that answered is worth asking again. MSSP data goes stale — the player
    /// count especially — but not in minutes, and re-crawling a server every few minutes is exactly the
    /// behaviour that gets a crawler firewalled. A day is the default; a server may ask for longer
    /// through <c>CRAWL DELAY</c> and that request is honoured (see <see cref="ResolveRevisit"/>).
    /// </summary>
    public TimeSpan RevisitInterval { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How long before a server that connected but offered no MSSP is worth asking again. Much longer
    /// than <see cref="RevisitInterval"/>, because a server that does not implement MSSP today will
    /// almost certainly not implement it tomorrow, and re-asking is pure cost to both sides.
    /// </summary>
    public TimeSpan NoMsspRevisitInterval { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// The backoff ladder after consecutive failures, indexed by failure count. A host that has failed
    /// more times than this list is long waits the last entry, until it is retired.
    /// </summary>
    public IReadOnlyList<TimeSpan> FailureBackoff { get; init; } =
    [
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(24),
        TimeSpan.FromDays(3),
    ];

    /// <summary>
    /// How many consecutive failures retire a host: it stays in the store, marked, and is never dialled
    /// again unless a human clears it. A host that has refused five times running is gone, and
    /// continuing to knock is the definition of impolite.
    /// </summary>
    public int RetireAfterFailures { get; init; } = 5;

    // ---- Hard caps on the run ----

    /// <summary>
    /// The most hosts one run may contact. Referral graphs are unbounded by construction; without this
    /// a misconfigured run walks as far as MSSP reaches, unattended.
    /// </summary>
    public int MaxHosts { get; init; } = 500;

    /// <summary>The longest one run may take, whatever else is still due.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How many referral hops from a seed a host may be discovered at. A third cap, and the one that
    /// bounds the <em>shape</em> of the crawl rather than its size: it keeps a run near the network the
    /// seeds describe instead of drifting arbitrarily far from it.
    /// </summary>
    public int MaxDepth { get; init; } = 4;

    // ---- Behaviour ----

    /// <summary>
    /// Whether to follow <c>REFERRAL</c> at all. Off makes this a status checker for a known list; on
    /// — the default — makes it a crawler.
    /// </summary>
    public bool FollowReferrals { get; init; } = true;

    /// <summary>
    /// Whether to follow a referral that names a loopback, private, link-local or multicast address.
    /// Off by default: such a referral is either a server misreporting itself or an attempt to aim
    /// somebody else's crawler at a network it cannot otherwise reach, and neither is worth a packet.
    /// </summary>
    public bool FollowPrivateAddresses { get; init; }

    /// <summary>
    /// Report what would be crawled and write nothing to the network. Every other limit still applies,
    /// so a dry run is also how a configuration is checked before it is trusted.
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// The name this crawler gives when a server asks what connected — the TTYPE/MTTS answers, most
    /// specific first.
    /// <para>
    /// A crawler has to be identifiable. A server operator reading their logs should be able to tell
    /// what this was and go and look up why, which is what the URL is for; the third entry is the MTTS
    /// bit vector, and it claims only UTF-8 (bit 4) because that is the only one of those capabilities
    /// this program actually has — it renders nothing, so claiming ANSI or 256 colours would be a
    /// small lie told to a stranger for no reason.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> TerminalTypes { get; init; } =
    [
        "SHARPMUTERM-MSSPCRAWLER",
        "SHARPMUTERM-MSSPCRAWLER (+https://github.com/SharpMUSH/SharpMUTerm)",
        "MTTS 4",
    ];

    /// <summary>
    /// When a host may next be visited after a successful crawl: this run's interval, or the server's
    /// own <c>CRAWL DELAY</c>, whichever is <em>longer</em>.
    /// <para>
    /// The specification calls <c>CRAWL DELAY</c> a "preferred minimum number of hours between crawls",
    /// so it is a floor a server asks for and taking the maximum honours it. It cannot be used to make
    /// us visit more often than we chose to: a server asking for one hour when this run's default is a
    /// day gets a day, because our own politeness setting is not theirs to lower.
    /// </para>
    /// </summary>
    public TimeSpan ResolveRevisit(TimeSpan? crawlDelay) =>
        crawlDelay is { } requested && requested > RevisitInterval ? requested : RevisitInterval;

    /// <summary>How long to wait before retrying a host that has failed <paramref name="failures"/> times running.</summary>
    public TimeSpan ResolveBackoff(int failures)
    {
        if (FailureBackoff.Count == 0)
        {
            return RevisitInterval;
        }

        var index = Math.Clamp(failures - 1, 0, FailureBackoff.Count - 1);
        return FailureBackoff[index];
    }

    /// <summary>
    /// Throws when a setting could only have arrived from a typo or a hand-edited file, rather than
    /// starting a run that would be wrong in a way nobody notices until it is on the network.
    /// </summary>
    public void Validate()
    {
        if (MaxConcurrency < 1)
        {
            throw new ArgumentException("max-concurrency must be at least 1.");
        }

        if (MaxHosts < 1)
        {
            throw new ArgumentException("max-hosts must be at least 1.");
        }

        if (MaxDepth < 0)
        {
            throw new ArgumentException("max-depth cannot be negative.");
        }

        if (GlobalInterval < TimeSpan.Zero || PerHostInterval < TimeSpan.Zero)
        {
            throw new ArgumentException("Rate-limit intervals cannot be negative.");
        }

        if (MaxDuration <= TimeSpan.Zero)
        {
            throw new ArgumentException("max-duration must be positive.");
        }

        if (ConnectTimeout <= TimeSpan.Zero || MsspTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("Timeouts must be positive.");
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            throw new ArgumentException("An output directory is required.");
        }
    }
}
