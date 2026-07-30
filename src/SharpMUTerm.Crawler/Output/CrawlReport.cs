using System.Globalization;
using System.Text;
using SharpMUTerm.Crawler.Model;
using SharpMUTerm.Crawler.Scheduling;

namespace SharpMUTerm.Crawler.Output;

/// <summary>Why a run stopped. The first line of the report, because it is the first thing to check.</summary>
public enum CrawlStopReason
{
    /// <summary>Nothing left that was due. The only ending that means the crawl finished.</summary>
    Exhausted,

    /// <summary><see cref="CrawlOptions.MaxHosts"/> was reached.</summary>
    HostCap,

    /// <summary><see cref="CrawlOptions.MaxDuration"/> elapsed.</summary>
    TimeCap,

    /// <summary>The operator interrupted it.</summary>
    Cancelled,
}

/// <summary>What one run did, as the run itself saw it.</summary>
public sealed record CrawlSummary
{
    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset FinishedAt { get; init; }

    public required CrawlStopReason StopReason { get; init; }

    public required IReadOnlyList<ProbeResult> Results { get; init; }

    public required IReadOnlyList<HostRecord> Hosts { get; init; }

    public required IReadOnlyDictionary<DiscoveryVerdict, int> Verdicts { get; init; }

    public int Contacted => Results.Count(result => result.Outcome != CrawlOutcome.Skipped);

    public int WithMssp => Results.Count(result => result.Succeeded);
}

/// <summary>
/// The human-readable output: a Markdown summary of the run.
/// <para>
/// Kept separate from <see cref="ObservationLog"/> on purpose. What a person wants is a short answer
/// to "what did this find, and did anything go wrong" — one row per server, sorted, with the failures
/// grouped and the referral graph's shape stated. What a program wants is every variable of every
/// observation, including the ones no report would print. Those are different documents, and trying
/// to make one file serve both produces something that is bad at each.
/// </para>
/// </summary>
public static class CrawlReport
{
    public static void Write(string path, CrawlSummary summary, CrawlOptions options)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // No byte-order mark: Encoding.UTF8 emits one, and it renders as a stray character at the head
        // of the first Markdown heading in most viewers.
        File.WriteAllText(path, Render(summary, options), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static string Render(CrawlSummary summary, CrawlOptions options)
    {
        var text = new StringBuilder();
        text.AppendLine("# MSSP crawl");
        text.AppendLine();
        text.AppendLine($"- Started: `{Stamp(summary.StartedAt)}`");
        text.AppendLine($"- Finished: `{Stamp(summary.FinishedAt)}` ({Duration(summary.FinishedAt - summary.StartedAt)})");
        text.AppendLine($"- Stopped because: **{Describe(summary.StopReason)}**");
        text.AppendLine($"- Hosts contacted: {summary.Contacted} of {options.MaxHosts} allowed");
        text.AppendLine($"- Answered with MSSP: {summary.WithMssp}");
        text.AppendLine($"- Known hosts after this run: {summary.Hosts.Count}");
        text.AppendLine();

        WriteOutcomes(text, summary);
        WriteServers(text, summary);
        WriteDiscovery(text, summary);
        WriteProblems(text, summary);
        WriteSchedule(text, summary);

        // Newlines are normalised to "\n" rather than left as StringBuilder.AppendLine wrote them.
        // AppendLine emits Environment.NewLine, so the same crawl produced different *bytes* on Windows
        // than on Linux — a report is an artefact people commit, diff and paste, and one that changes
        // shape with the machine that made it is worth nothing as a baseline. Markdown renders "\n"
        // everywhere, so nothing is lost.
        //
        // Safe as a blanket replace because no cell can carry a carriage return of its own: Cell strips
        // control characters out of everything that came off the wire, which is the point of the escape
        // test beside this one.
        return text.Replace("\r\n", "\n").ToString();
    }

    private static void WriteOutcomes(StringBuilder text, CrawlSummary summary)
    {
        if (summary.Results.Count == 0)
        {
            return;
        }

        text.AppendLine("## Outcomes");
        text.AppendLine();
        text.AppendLine("| Outcome | Hosts |");
        text.AppendLine("|---|--:|");
        foreach (var group in summary.Results.GroupBy(result => result.Outcome).OrderByDescending(g => g.Count()))
        {
            text.AppendLine($"| {group.Key} | {group.Count()} |");
        }

        text.AppendLine();
    }

    private static void WriteServers(StringBuilder text, CrawlSummary summary)
    {
        var found = summary.Results.Where(result => result.Succeeded && result.Data is not null).ToList();
        if (found.Count == 0)
        {
            return;
        }

        text.AppendLine("## Servers");
        text.AppendLine();
        text.AppendLine("| Name | Address | Players | Codebase | Up since | Referrals | Seen |");
        text.AppendLine("|---|---|--:|---|---|--:|---|");

        foreach (var result in found.OrderBy(r => r.Data!.Name ?? r.Host.Host, StringComparer.OrdinalIgnoreCase))
        {
            var data = result.Data!;
            text.Append("| ").Append(Cell(data.Name ?? "_(unnamed)_"))
                .Append(" | ").Append(Cell(result.Host.ToString()))
                .Append(" | ").Append(data.Players?.ToString(CultureInfo.InvariantCulture) ?? "—")
                .Append(" | ").Append(Cell(data.Codebase ?? "—"))
                .Append(" | ").Append(data.Uptime is { } up ? Stamp(up) : "—")
                .Append(" | ").Append(data.Referrals.Count)
                .Append(" | ").Append(Stamp(result.ObservedAt))
                .AppendLine(" |");
        }

        text.AppendLine();
    }

    private static void WriteDiscovery(StringBuilder text, CrawlSummary summary)
    {
        if (summary.Verdicts.Count == 0)
        {
            return;
        }

        text.AppendLine("## Referrals");
        text.AppendLine();
        text.AppendLine("What was done with every host named by a `REFERRAL` list, and by the seeds.");
        text.AppendLine();
        text.AppendLine("| Verdict | Count |");
        text.AppendLine("|---|--:|");
        foreach (var (verdict, count) in summary.Verdicts.OrderByDescending(pair => pair.Value))
        {
            text.AppendLine($"| {Describe(verdict)} | {count} |");
        }

        text.AppendLine();

        var discovered = summary.Hosts.Where(host => host.DiscoveredFrom is not null).ToList();
        if (discovered.Count == 0)
        {
            return;
        }

        text.AppendLine("### Discovered by referral");
        text.AppendLine();
        text.AppendLine("| Host | Depth | Referred by | Status |");
        text.AppendLine("|---|--:|---|---|");
        foreach (var host in discovered.OrderBy(h => h.Depth).ThenBy(h => h.Host.Host, StringComparer.Ordinal))
        {
            text.Append("| ").Append(Cell(host.Host.ToString()))
                .Append(" | ").Append(host.Depth)
                .Append(" | ").Append(Cell(host.DiscoveredFrom!.ToString()))
                .Append(" | ").Append(host.LastOutcome == CrawlOutcome.Unknown ? "not yet visited" : host.LastOutcome.ToString())
                .AppendLine(" |");
        }

        text.AppendLine();
    }

    private static void WriteProblems(StringBuilder text, CrawlSummary summary)
    {
        var problems = summary.Results.Where(result => result.IsFailure).ToList();
        if (problems.Count == 0)
        {
            return;
        }

        text.AppendLine("## Failures");
        text.AppendLine();
        text.AppendLine("| Address | Outcome | Detail | Consecutive |");
        text.AppendLine("|---|---|---|--:|");

        var byHost = summary.Hosts.ToDictionary(host => host.Host);
        foreach (var result in problems.OrderBy(r => r.Host.Host, StringComparer.Ordinal))
        {
            var consecutive = byHost.TryGetValue(result.Host, out var record) ? record.ConsecutiveFailures : 0;
            text.Append("| ").Append(Cell(result.Host.ToString()))
                .Append(" | ").Append(result.Outcome)
                .Append(" | ").Append(Cell(result.Error ?? "—"))
                .Append(" | ").Append(consecutive)
                .AppendLine(" |");
        }

        text.AppendLine();

        var retired = summary.Hosts.Where(host => host.Retired).ToList();
        if (retired.Count > 0)
        {
            text.AppendLine($"{retired.Count} host(s) have been retired and will not be contacted again:");
            text.AppendLine();
            foreach (var host in retired.OrderBy(h => h.Host.Host, StringComparer.Ordinal))
            {
                text.AppendLine($"- `{host.Host}` — {host.ConsecutiveFailures} consecutive failures, last: {host.LastError ?? "unknown"}");
            }

            text.AppendLine();
        }
    }

    private static void WriteSchedule(StringBuilder text, CrawlSummary summary)
    {
        var scheduled = summary.Hosts
            .Where(host => !host.Retired && host.NotBefore is not null)
            .OrderBy(host => host.NotBefore)
            .Take(20)
            .ToList();

        if (scheduled.Count == 0)
        {
            return;
        }

        text.AppendLine("## Next due");
        text.AppendLine();
        text.AppendLine("The next run picks up from here; nothing below is contacted before the time shown.");
        text.AppendLine();
        text.AppendLine("| Address | Not before | Last outcome |");
        text.AppendLine("|---|---|---|");
        foreach (var host in scheduled)
        {
            text.Append("| ").Append(Cell(host.Host.ToString()))
                .Append(" | ").Append(Stamp(host.NotBefore!.Value))
                .Append(" | ").Append(host.LastOutcome)
                .AppendLine(" |");
        }

        text.AppendLine();
    }

    private static string Describe(CrawlStopReason reason) => reason switch
    {
        CrawlStopReason.Exhausted => "nothing left that was due",
        CrawlStopReason.HostCap => "the host cap was reached",
        CrawlStopReason.TimeCap => "the time cap was reached",
        CrawlStopReason.Cancelled => "it was interrupted",
        _ => reason.ToString(),
    };

    private static string Describe(DiscoveryVerdict verdict) => verdict switch
    {
        DiscoveryVerdict.Added => "added to the crawl",
        DiscoveryVerdict.AlreadyKnown => "already known (a cycle, or two servers naming the same peer)",
        DiscoveryVerdict.SelfReferral => "the server referred to itself",
        DiscoveryVerdict.TooDeep => "beyond the depth limit",
        DiscoveryVerdict.NotRoutable => "not a public address",
        DiscoveryVerdict.ReferralsDisabled => "referrals were not being followed",
        _ => verdict.ToString(),
    };

    private static string Stamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Duration(TimeSpan value) =>
        value.TotalMinutes < 1
            ? $"{value.TotalSeconds:F1}s"
            : $"{(int)value.TotalMinutes}m {value.Seconds}s";

    /// <summary>
    /// Makes a value safe to put in a Markdown table cell. Every string here came off the wire from a
    /// stranger's server — a MUD's name is whatever its owner typed — so a pipe or a newline in it must
    /// not be able to break the table it is printed in.
    /// </summary>
    private static string Cell(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
