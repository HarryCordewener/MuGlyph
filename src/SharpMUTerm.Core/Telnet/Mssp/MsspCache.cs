using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SharpMUTerm.Core.Telnet.Mssp;

/// <summary>
/// What every server we have connected to said about itself, kept so the INFO screen can answer while
/// nothing is connected — which is when the question is asked.
/// <para>
/// <b>Not in <c>config.json</c>.</b> That file holds what the user asked for and is one people hand-edit;
/// this holds what a stranger's server said, and a write per connect would make opening a socket dirty
/// somebody's settings. It is a sibling cache file — the shape <c>SecretsStore</c> and the restore log
/// already have — versioned on its own, so nothing here can force an <c>AppConfiguration</c> migration.
/// </para>
/// <para>
/// <b>Keyed by <c>host:port</c>, not by world.</b> MSSP describes a <em>server</em>; a world name is a
/// user-editable label that two entries may share and that a rename changes out from under a report that
/// has not. Keying by endpoint means two worlds pointed at one server read one report, renaming a world
/// keeps it, and a world repointed at a different host correctly stops seeing the old server's answer.
/// The cost is the one case where it reads oddly — a world whose port is edited from 4000 to 4001 looks
/// as though its report vanished — and that reading is right: those are two endpoints, they may run
/// different games, and one of them is the plaintext port and the other the TLS one.
/// </para>
/// <para>
/// <b>A second report replaces the first outright.</b> MSSP is not a delta protocol: a server sends its
/// whole table once per connection, so merging would keep variables it has stopped publishing for ever —
/// the same failure as a merged room-exit list, in a different costume. Worse, a merged report is a
/// snapshot of no moment that ever existed, which is precisely what a dated report exists not to be. So
/// the newest whole report wins, and <see cref="MsspObservation.ObservedAt"/> moves with it.
/// </para>
/// <para>
/// <b>Persistence is optional and the default is memory-only.</b> A cache built with no path reads
/// nothing and writes nothing, which is what every test and every <c>--snapshot</c> gets: the guarantee
/// is structural rather than a null check at each use site, so there is no third call site to forget.
/// Only <c>Program</c> hands one a path.
/// </para>
/// <para>
/// Nothing here throws at a caller. A cache file that cannot be read starts empty and says so through
/// <see cref="Problem"/>; one that cannot be written degrades to memory-only for the session.
/// </para>
/// </summary>
public sealed class MsspCache
{
    /// <summary>The file, beside <c>config.json</c>.</summary>
    public const string FileName = "mssp.json";

    /// <summary>This file's own schema version, independent of the configuration's.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// How many endpoints are kept. A client that has connected to two hundred distinct servers is not
    /// a client whose two-hundred-and-first lookup is worth an unbounded file; the least recently
    /// reached entry is dropped. The bound is in <em>entries</em> and not bytes because the per-entry
    /// bounds below already make an entry finite.
    /// </summary>
    public const int MaxEndpoints = 200;

    /// <summary>
    /// How many variables of one report are kept. The specification defines about forty-five and a
    /// generous server invents a handful more; this is several times that, and it is here because MSSP
    /// has no payload cap upstream (<c>SubnegotiationBuffer</c> guards GMCP, MSDP and CHARSET's TTABLE
    /// and not this), so the number of variables a hostile server can put in one report is its choice.
    /// </summary>
    public const int MaxVariables = 128;

    /// <summary>How many values of one variable are kept. <c>PORT</c> and <c>REFERRAL</c> use a handful.</summary>
    public const int MaxValuesPerVariable = 16;

    /// <summary>
    /// How long one value may be. Bounding at the door rather than only at the renderer matters because
    /// this file is written, read back and rendered: a megabyte <c>NAME</c> that only the screen trimmed
    /// would still be a megabyte on disk and a megabyte in memory for every later launch.
    /// </summary>
    public const int MaxValueLength = 512;

    private readonly string? _path;
    private readonly Dictionary<string, MsspObservation> _observations = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private bool _writable = true;

    /// <summary>
    /// Builds a cache. <paramref name="path"/> null — the default — is memory-only: nothing is read and
    /// nothing is ever written, which is the state a snapshot and every test runs in.
    /// </summary>
    public MsspCache(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        if (_path is not null)
        {
            Problem = Load(_path, _observations);
        }
    }

    /// <summary>The cache file beside a configuration file, the way <c>SecretsStore.PathFor</c> resolves.</summary>
    public static string PathFor(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(configurationPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(configurationPath));
        return Path.Combine(directory ?? string.Empty, FileName);
    }

    /// <summary>Diagnostics for anything this cache does on disk.</summary>
    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>Why the file on disk could not be read, or null when there was nothing wrong with it.</summary>
    public string? Problem { get; }

    /// <summary>Whether this cache has a file at all; false means memory-only, by construction.</summary>
    public bool IsPersistent => _path is not null;

    /// <summary>Every endpoint on record, most recently reached first.</summary>
    public IReadOnlyList<MsspObservation> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _observations.Values.OrderByDescending(o => o.ConnectedAt)];
            }
        }
    }

    /// <summary>
    /// The identity a report is filed under: the host lower-cased with any trailing root dot removed,
    /// a colon, and the port. Normalising means <c>MUD.Example.ORG.</c> and <c>mud.example.org</c> are
    /// one server rather than two half-filled entries — the same folding a hostname comparison would do,
    /// done once, here, so no caller has to remember to.
    /// </summary>
    public static string Key(string? host, int port) =>
        string.Concat(
            (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant(),
            ":",
            port.ToString(CultureInfo.InvariantCulture));

    /// <summary>What this endpoint last said, or null when nothing here has ever reached it.</summary>
    public MsspObservation? Find(string? host, int port)
    {
        var key = Key(host, port);
        lock (_gate)
        {
            return _observations.GetValueOrDefault(key);
        }
    }

    /// <summary>
    /// Records that a session reached this endpoint, without a report. This is what separates "connected
    /// and the server publishes nothing" from "never connected", and it is the reason the screen can say
    /// the first out loud instead of showing the same emptiness for both.
    /// <para>
    /// It never clears an existing report. A server that published MSSP once and has stopped is still a
    /// server whose report we hold, dated when it arrived; blanking it on the next connect would throw
    /// away the only answer we have in order to be freshly ignorant.
    /// </para>
    /// </summary>
    public void RecordConnection(string? host, int port, DateTimeOffset at)
    {
        var key = Key(host, port);
        lock (_gate)
        {
            var existing = _observations.GetValueOrDefault(key);
            _observations[key] = existing is null
                ? new MsspObservation(key, at, null, null)
                : existing with { ConnectedAt = at };
            Persist();
        }
    }

    /// <summary>
    /// Records a report from this endpoint, replacing whatever was there. See the type summary for why
    /// replacement rather than a merge, and <see cref="Clamp"/> for what is kept of a hostile one.
    /// </summary>
    public void RecordReport(string? host, int port, MsspData report, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(report);

        var key = Key(host, port);
        var clamped = Clamp(report);
        lock (_gate)
        {
            var connected = _observations.GetValueOrDefault(key)?.ConnectedAt ?? at;
            _observations[key] = new MsspObservation(key, connected, clamped, at);
            Persist();
        }
    }

    /// <summary>
    /// Cuts a report down to what this cache will hold: <see cref="MaxVariables"/> variables, each with
    /// at most <see cref="MaxValuesPerVariable"/> values of at most <see cref="MaxValueLength"/>
    /// characters. Order is preserved, so what survives is the head of what the server sent rather than
    /// an arbitrary subset.
    /// <para>
    /// Truncation happens <em>here</em>, before anything is stored or written, and not only in the
    /// renderer. A value the screen trimmed would still have been read off the wire, held in memory,
    /// written to disk and read back on every later launch at full size; bounding at the door is the
    /// only place that costs a hostile server anything.
    /// </para>
    /// </summary>
    private static MsspData Clamp(MsspData report)
    {
        var kept = new List<KeyValuePair<string, IReadOnlyList<string>>>();
        foreach (var (name, values) in report)
        {
            if (kept.Count >= MaxVariables)
            {
                break;
            }

            var trimmed = new List<string>(Math.Min(values.Count, MaxValuesPerVariable));
            foreach (var value in values.Take(MaxValuesPerVariable))
            {
                trimmed.Add(value.Length > MaxValueLength ? value[..MaxValueLength] : value);
            }

            kept.Add(new KeyValuePair<string, IReadOnlyList<string>>(name, trimmed));
        }

        return MsspData.From(kept);
    }

    // ---- Disk ----

    private void Persist()
    {
        // Bounded first, and outside the early return below, because a memory-only cache is bounded
        // too: the file is one reason for the cap and the process's own memory is the other, and a
        // long-running client that dialled a thousand servers should not hold a thousand reports just
        // because it happens to own no file.
        Evict();

        if (_path is null || !_writable)
        {
            return;
        }

        try
        {
            var document = new JsonObject
            {
                ["version"] = CurrentVersion,
                ["servers"] = Servers(),
            };

            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? string.Empty);

            // Temp file plus rename: a launch that is interrupted mid-write finds either the old file or
            // the new one, never half of one. This is derived data and a truncated file would only cost a
            // reset — but the same is true of the restore log, which does this, and a cache that can
            // corrupt itself is a cache somebody eventually debugs.
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Memory-only from here. Retrying per connect would log the same failure for ever.
            _writable = false;
            Logger.LogWarning(e, "MSSP cache is not writable at {Path}; keeping reports in memory only.", _path);
        }
    }

    private JsonObject Servers()
    {
        var servers = new JsonObject();
        foreach (var observation in _observations.Values.OrderBy(o => o.Endpoint, StringComparer.Ordinal))
        {
            var entry = new JsonObject
            {
                ["connectedAt"] = observation.ConnectedAt.ToString("O", CultureInfo.InvariantCulture),
            };

            if (observation is { Report: { } report, ObservedAt: { } observed })
            {
                entry["observedAt"] = observed.ToString("O", CultureInfo.InvariantCulture);

                // An *array* of name/value pairs rather than an object, because MSSP's order is meaning:
                // "multiple values should be ordered from least to most relevant", and a JSON object's
                // member order is not something a reader is obliged to preserve.
                var variables = new JsonArray();
                foreach (var (name, values) in report)
                {
                    var list = new JsonArray();
                    foreach (var value in values)
                    {
                        list.Add(value);
                    }

                    variables.Add(new JsonObject { ["name"] = name, ["values"] = list });
                }

                entry["variables"] = variables;
            }

            servers[observation.Endpoint] = entry;
        }

        return servers;
    }

    /// <summary>Drops the least recently reached endpoints until the file is back within its bound.</summary>
    private void Evict()
    {
        if (_observations.Count <= MaxEndpoints)
        {
            return;
        }

        foreach (var stale in _observations.Values
            .OrderByDescending(o => o.ConnectedAt)
            .Skip(MaxEndpoints)
            .Select(o => o.Endpoint)
            .ToList())
        {
            _observations.Remove(stale);
        }
    }

    /// <summary>
    /// Reads the file into <paramref name="into"/>, returning what was wrong with it or null. A missing
    /// file is not wrong. Neither is a single malformed entry: it is skipped and counted, because one
    /// bad line in a cache of two hundred servers is not a reason to forget the other hundred and
    /// ninety-nine.
    /// </summary>
    private static string? Load(string path, Dictionary<string, MsspObservation> into)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
            {
                return $"{FileName} is not a JSON object; starting with no cached server information.";
            }

            var version = root["version"]?.GetValue<int>() ?? 0;
            if (version > CurrentVersion)
            {
                return $"{FileName} was written by a newer version of SharpMUTerm "
                    + $"(schema {version.ToString(CultureInfo.InvariantCulture)}); ignoring it.";
            }

            if (root["servers"] is not JsonObject servers)
            {
                return null;
            }

            var skipped = 0;
            foreach (var (endpoint, node) in servers)
            {
                if (node is not JsonObject entry || Read(endpoint, entry) is not { } observation)
                {
                    skipped++;
                    continue;
                }

                into[endpoint] = observation;
            }

            return skipped == 0
                ? null
                : $"{FileName}: {skipped.ToString(CultureInfo.InvariantCulture)} unreadable entries skipped.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return $"{FileName} could not be read ({e.GetType().Name}); starting with no cached server information.";
        }
    }

    private static MsspObservation? Read(string endpoint, JsonObject entry)
    {
        if (endpoint.Length == 0
            || entry["connectedAt"]?.GetValue<string>() is not { } connectedText
            || !DateTimeOffset.TryParse(
                connectedText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var connectedAt))
        {
            return null;
        }

        if (entry["observedAt"]?.GetValue<string>() is not { } observedText
            || !DateTimeOffset.TryParse(
                observedText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var observedAt)
            || entry["variables"] is not JsonArray variables)
        {
            return new MsspObservation(endpoint, connectedAt, null, null);
        }

        var pairs = new List<KeyValuePair<string, IReadOnlyList<string>>>();
        foreach (var variable in variables)
        {
            if (variable is not JsonObject pair
                || pair["name"]?.GetValue<string>() is not { Length: > 0 } name)
            {
                continue;
            }

            var values = new List<string>();
            if (pair["values"] is JsonArray list)
            {
                foreach (var value in list)
                {
                    if (value?.GetValue<string>() is { } text)
                    {
                        values.Add(text.Length > MaxValueLength ? text[..MaxValueLength] : text);
                    }
                }
            }

            pairs.Add(new KeyValuePair<string, IReadOnlyList<string>>(name, values));
            if (pairs.Count >= MaxVariables)
            {
                break;
            }
        }

        return new MsspObservation(endpoint, connectedAt, MsspData.From(pairs), observedAt);
    }
}
