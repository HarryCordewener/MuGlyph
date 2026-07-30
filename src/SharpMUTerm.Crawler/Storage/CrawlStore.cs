using System.Text.Json;
using System.Text.Json.Serialization;
using SharpMUTerm.Core.Telnet.Mssp;
using SharpMUTerm.Crawler.Model;

namespace SharpMUTerm.Crawler.Storage;

/// <summary>
/// The crawl's memory between runs: every host it has ever heard of, how each attempt went, and when
/// each may next be attempted.
/// <para>
/// Without this a second run is a first run: it would re-dial every host it had just visited, the
/// revisit interval would describe nothing, and a host that has refused a hundred times would be
/// refused a hundred and one. The whole of the politeness story depends on the crawler remembering,
/// so the store is written after every observation rather than at the end of a run — a crawl killed
/// half way through must not forget the half it did.
/// </para>
/// <para>
/// It is a plain JSON file under the run's own output directory. Never the user's configuration
/// directory: this tool reads no configuration of the client's and writes none.
/// </para>
/// </summary>
public sealed class CrawlStore(string path)
{
    /// <summary>Bumped when the on-disk shape changes in a way an older file cannot be read as.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // camelCase, matched case-insensitively on the way in, so a file written by an older build or
        // edited by hand still loads.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Path { get; } = path;

    /// <summary>
    /// Reads the previous run's state, or returns nothing when there is none.
    /// <para>
    /// A file that cannot be parsed — a truncated write, a version from the future — is reported and
    /// treated as absent rather than throwing. Losing the memory of a crawl costs one round of extra
    /// politeness; refusing to start because of it costs the run.
    /// </para>
    /// </summary>
    public IReadOnlyList<HostRecord> Load(out string? problem)
    {
        problem = null;
        if (!File.Exists(Path))
        {
            return [];
        }

        try
        {
            var document = JsonSerializer.Deserialize<StateDocument>(File.ReadAllText(Path), Json);
            if (document is null)
            {
                problem = "the state file was empty";
                return [];
            }

            if (document.Version > CurrentVersion)
            {
                problem = $"the state file is version {document.Version}, newer than this build understands";
                return [];
            }

            return document.Hosts.Select(FromEntry).OfType<HostRecord>().ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            problem = $"the state file could not be read ({ex.GetType().Name})";
            return [];
        }
    }

    /// <summary>
    /// Writes the state, atomically: to a temporary file beside the real one, then replaced over it.
    /// A crawl that is killed mid-write must not leave a state file that cannot be read, because the
    /// consequence of losing it is re-visiting everyone.
    /// </summary>
    public void Save(IEnumerable<HostRecord> records)
    {
        var document = new StateDocument
        {
            Version = CurrentVersion,
            SavedAt = DateTimeOffset.UtcNow,
            Hosts = records.Select(ToEntry).OrderBy(entry => entry.Host, StringComparer.Ordinal).ToList(),
        };

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = Path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, Json));
        File.Move(temporary, Path, overwrite: true);
    }

    private static HostEntry ToEntry(HostRecord record) => new()
    {
        Host = record.Host.Host,
        Port = record.Host.Port,
        Depth = record.Depth,
        DiscoveredFrom = record.DiscoveredFrom?.ToReferralString(),
        FirstSeen = record.FirstSeen,
        LastAttempt = record.LastAttempt,
        LastSuccess = record.LastSuccess,
        LastOutcome = record.LastOutcome,
        LastError = record.LastError,
        Name = record.Name,
        CrawlDelayHours = record.CrawlDelayHours,
        ConsecutiveFailures = record.ConsecutiveFailures,
        Attempts = record.Attempts,
        NotBefore = record.NotBefore,
        Retired = record.Retired,
    };

    private static HostRecord? FromEntry(HostEntry entry)
    {
        // Re-normalising through MsspHost.Create rather than trusting the file: a state file written by
        // an older build (or edited by hand) may hold a spelling that is no longer canonical, and two
        // spellings of one host in the frontier is exactly the bug the normalisation exists to prevent.
        if (MsspHost.Create(entry.Host, entry.Port) is not { } host)
        {
            return null;
        }

        MsspHost.TryParse(entry.DiscoveredFrom, out var from);

        return new HostRecord
        {
            Host = host,
            Depth = entry.Depth,
            DiscoveredFrom = from,
            FirstSeen = entry.FirstSeen,
            LastAttempt = entry.LastAttempt,
            LastSuccess = entry.LastSuccess,
            LastOutcome = entry.LastOutcome,
            LastError = entry.LastError,
            Name = entry.Name,
            CrawlDelayHours = entry.CrawlDelayHours,
            ConsecutiveFailures = entry.ConsecutiveFailures,
            Attempts = entry.Attempts,
            NotBefore = entry.NotBefore,
            Retired = entry.Retired,
        };
    }

    private sealed class StateDocument
    {
        public int Version { get; set; }

        public DateTimeOffset SavedAt { get; set; }

        public List<HostEntry> Hosts { get; set; } = [];
    }

    private sealed class HostEntry
    {
        public string Host { get; set; } = string.Empty;

        public int Port { get; set; }

        public int Depth { get; set; }

        public string? DiscoveredFrom { get; set; }

        public DateTimeOffset FirstSeen { get; set; }

        public DateTimeOffset? LastAttempt { get; set; }

        public DateTimeOffset? LastSuccess { get; set; }

        public CrawlOutcome LastOutcome { get; set; }

        public string? LastError { get; set; }

        public string? Name { get; set; }

        public double? CrawlDelayHours { get; set; }

        public int ConsecutiveFailures { get; set; }

        public int Attempts { get; set; }

        public DateTimeOffset? NotBefore { get; set; }

        public bool Retired { get; set; }
    }
}
