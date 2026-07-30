using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpMUTerm.Crawler.Model;

namespace SharpMUTerm.Crawler.Output;

/// <summary>
/// The machine-readable output: one JSON object per line, appended, one line per observation.
/// <para>
/// Newline-delimited JSON rather than one big document because a crawl is a stream — it is appended
/// to while the run is going, it survives being killed half way, and a consumer can read the first
/// record without the last. A single JSON array would have none of those properties.
/// </para>
/// <para>
/// <b>Every record carries when it was taken.</b> MSSP is a status protocol: <c>PLAYERS</c> is true
/// for a moment and <c>UPTIME</c> is only meaningful relative to when it was read. A record without a
/// timestamp is not stale data, it is data of unknown age, which is worse.
/// </para>
/// <para>
/// Variables are written as the protocol sends them — the canonical name against an <em>array</em> of
/// values — so <c>REFERRAL</c> and a multi-valued <c>PORT</c> survive the round trip. Flattening to
/// one value per key here would lose the arrays MSSP exists to publish.
/// </para>
/// </summary>
public sealed class ObservationLog(string path) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // camelCase, matched case-insensitively on the way in, so a file written by an older build or
        // edited by hand still loads.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },

        // Relaxed escaping so a MUD's name, website or contact address reads as itself in the file
        // rather than as a run of \uXXXX. The output is a data file, not a web page.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly StreamWriter _writer = Open(path);

    private readonly Lock _gate = new();

    public string Path { get; } = path;

    public void Append(ProbeResult result)
    {
        var record = new ObservationRecord
        {
            ObservedAt = result.ObservedAt,
            Host = result.Host.Host,
            Port = result.Host.Port,
            Outcome = result.Outcome,
            DurationMs = (long)result.Duration.TotalMilliseconds,
            Error = result.Error,
            Name = result.Data?.Name,
            Players = result.Data?.Players,
            Uptime = result.Data?.Uptime,
            CrawlDelayHours = result.Data?.CrawlDelay?.TotalHours,
            Referrals = result.Data?.Referrals.Select(host => host.ToReferralString()).ToList(),
            Variables = result.Data?.ToDictionary(pair => pair.Key, pair => pair.Value),
        };

        var line = JsonSerializer.Serialize(record, Json);
        lock (_gate)
        {
            _writer.WriteLine(line);
            // Flushed per record: a crawl that is interrupted must keep everything it had already
            // observed, and an observation is expensive enough (a whole connection) that the cost of a
            // flush against it is nothing.
            _writer.Flush();
        }
    }

    public void Dispose() => _writer.Dispose();

    /// <summary>
    /// The share mode names <see cref="FileShare.Delete"/> deliberately. A crawl can run for hours, and
    /// the observation log is a file the operator tails, rotates and clears while it does — on Windows a
    /// file opened without it can be neither deleted nor renamed nor its directory removed until the
    /// crawler exits, which POSIX allows unasked. This is the same omission the spill, the restore log
    /// and both transcript sinks carried; the Windows CI job runs no Crawler step, so nothing here would
    /// have caught it.
    /// </summary>
    private const FileShare LogShare = FileShare.Read | FileShare.Delete;

    private static StreamWriter Open(string path)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // UTF-8 with *no* byte-order mark. Encoding.UTF8 emits one, and a BOM at the head of a
        // newline-delimited JSON file breaks the first record for every consumer that does not know to
        // strip it — which is most of them, including Python's own json module.
        return new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, LogShare),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private sealed class ObservationRecord
    {
        public DateTimeOffset ObservedAt { get; init; }

        public string Host { get; init; } = string.Empty;

        public int Port { get; init; }

        public CrawlOutcome Outcome { get; init; }

        public long DurationMs { get; init; }

        public string? Error { get; init; }

        public string? Name { get; init; }

        public int? Players { get; init; }

        public DateTimeOffset? Uptime { get; init; }

        public double? CrawlDelayHours { get; init; }

        public IReadOnlyList<string>? Referrals { get; init; }

        public IReadOnlyDictionary<string, IReadOnlyList<string>>? Variables { get; init; }
    }
}
