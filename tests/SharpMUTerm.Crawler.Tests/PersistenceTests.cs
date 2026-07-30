using System.Text.Json;
using SharpMUTerm.Core.Telnet.Mssp;
using SharpMUTerm.Crawler.Model;
using SharpMUTerm.Crawler.Output;
using SharpMUTerm.Crawler.Scheduling;
using SharpMUTerm.Crawler.Storage;
using SharpMUTerm.Crawler.Tests.Support;

namespace SharpMUTerm.Crawler.Tests;

/// <summary>
/// The state file and the two outputs. Everything here writes into a temporary directory of its own
/// and deletes it: this tool must never write anywhere near the user's configuration.
/// </summary>
public class PersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static MsspHost Host(string name, int port = 4201) => MsspHost.Create(name, port)!;

    private static string Scratch()
    {
        var path = Path.Combine(Path.GetTempPath(), "sharpmuterm-crawler-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Test]
    public async Task StateSurvivesARoundTripSoASecondRunResumes()
    {
        var directory = Scratch();
        try
        {
            var store = new CrawlStore(Path.Combine(directory, "state.json"));
            var records = new[]
            {
                new HostRecord
                {
                    Host = Host("a.example.org"),
                    Depth = 0,
                    FirstSeen = Now,
                    LastAttempt = Now,
                    LastSuccess = Now,
                    LastOutcome = CrawlOutcome.MsspReceived,
                    Name = "Corvid Nest",
                    CrawlDelayHours = 5,
                    Attempts = 3,
                    NotBefore = Now + TimeSpan.FromHours(24),
                },
                new HostRecord
                {
                    Host = Host("b.example.org", 4000),
                    Depth = 2,
                    DiscoveredFrom = Host("a.example.org"),
                    FirstSeen = Now,
                    LastOutcome = CrawlOutcome.ConnectFailed,
                    LastError = "ConnectionRefused",
                    ConsecutiveFailures = 2,
                    NotBefore = Now + TimeSpan.FromHours(6),
                },
                new HostRecord
                {
                    Host = Host("dead.example.org"),
                    FirstSeen = Now,
                    Retired = true,
                    ConsecutiveFailures = 5,
                },
            };

            store.Save(records);
            var loaded = store.Load(out var problem);

            await Assert.That(problem).IsNull();
            await Assert.That(loaded.Count).IsEqualTo(3);

            var a = loaded.Single(r => r.Host == Host("a.example.org"));
            await Assert.That(a.Name).IsEqualTo("Corvid Nest");
            await Assert.That(a.CrawlDelayHours).IsEqualTo(5);
            await Assert.That(a.NotBefore).IsEqualTo(Now + TimeSpan.FromHours(24));
            await Assert.That(a.IsDue(Now)).IsFalse();
            await Assert.That(a.IsDue(Now + TimeSpan.FromHours(25))).IsTrue();

            var b = loaded.Single(r => r.Host == Host("b.example.org", 4000));
            await Assert.That(b.DiscoveredFrom).IsEqualTo(Host("a.example.org"));
            await Assert.That(b.ConsecutiveFailures).IsEqualTo(2);

            var retired = loaded.Single(r => r.Host == Host("dead.example.org"));
            await Assert.That(retired.Retired).IsTrue();
            await Assert.That(retired.IsDue(Now + TimeSpan.FromDays(365))).IsFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task AHostSpelledDifferentlyInTheFileIsNormalisedOnTheWayBackIn()
    {
        // A state file written by an older build, or edited by hand. Two spellings of one host in the
        // frontier is exactly the duplicate the normalisation exists to prevent.
        var directory = Scratch();
        try
        {
            var path = Path.Combine(directory, "state.json");
            File.WriteAllText(path, """
                {
                  "version": 1,
                  "savedAt": "2026-01-01T00:00:00+00:00",
                  "hosts": [
                    { "host": "A.Example.ORG.", "port": 4201, "firstSeen": "2026-01-01T00:00:00+00:00" },
                    { "host": "2001:0DB8:0000::0001", "port": 4201, "firstSeen": "2026-01-01T00:00:00+00:00" },
                    { "host": "", "port": 4201, "firstSeen": "2026-01-01T00:00:00+00:00" }
                  ]
                }
                """);

            var loaded = new CrawlStore(path).Load(out var problem);

            await Assert.That(problem).IsNull();

            // The unusable entry is dropped rather than carried as a host that can never be dialled.
            await Assert.That(loaded.Count).IsEqualTo(2);
            await Assert.That(loaded.Select(r => r.Host.Host))
                .IsEquivalentTo(new[] { "a.example.org", "2001:db8::1" });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task AnUnreadableStateFileIsReportedAndTreatedAsAbsent()
    {
        var directory = Scratch();
        try
        {
            var path = Path.Combine(directory, "state.json");
            File.WriteAllText(path, "{ this is not json");

            var loaded = new CrawlStore(path).Load(out var problem);

            await Assert.That(loaded).IsEmpty();
            await Assert.That(problem).IsNotNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task AStateFileFromAFutureVersionIsRefusedRatherThanMisread()
    {
        var directory = Scratch();
        try
        {
            var path = Path.Combine(directory, "state.json");
            File.WriteAllText(path, """{ "version": 99, "hosts": [] }""");

            new CrawlStore(path).Load(out var problem);
            await Assert.That(problem).Contains("newer than this build");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task TheObservationLogRecordsWhenEachRecordWasSeenAndKeepsEveryArray()
    {
        var directory = Scratch();
        try
        {
            var path = Path.Combine(directory, "observations.jsonl");
            var data = MsspWire.Report(MsspWire.RepresentativeReport("peer.example.net 4000"));

            using (var log = new ObservationLog(path))
            {
                log.Append(new ProbeResult
                {
                    Host = Host("a.example.org"),
                    Outcome = CrawlOutcome.MsspReceived,
                    ObservedAt = Now,
                    Duration = TimeSpan.FromMilliseconds(412),
                    Data = data,
                });

                log.Append(new ProbeResult
                {
                    Host = Host("b.example.org"),
                    Outcome = CrawlOutcome.ConnectFailed,
                    ObservedAt = Now + TimeSpan.FromSeconds(2),
                    Error = "ConnectionRefused",
                });
            }

            var lines = File.ReadAllLines(path);
            await Assert.That(lines.Length).IsEqualTo(2);

            using var first = JsonDocument.Parse(lines[0]);
            var root = first.RootElement;

            // MSSP goes stale; a record without a timestamp is data of unknown age.
            await Assert.That(root.GetProperty("observedAt").GetDateTimeOffset()).IsEqualTo(Now);
            await Assert.That(root.GetProperty("host").GetString()).IsEqualTo("a.example.org");
            await Assert.That(root.GetProperty("players").GetInt32()).IsEqualTo(17);

            // Arrays survive to the file, which is the whole reason the model holds lists.
            var ports = root.GetProperty("variables").GetProperty("PORT").EnumerateArray()
                .Select(v => v.GetString() ?? string.Empty).ToArray();
            await Assert.That(ports).IsEquivalentTo(new[] { "80", "23", "4201" });

            // Including the variables no model knows about.
            await Assert.That(root.GetProperty("variables").GetProperty("CORVID SPECIFIC")[0].GetString())
                .IsEqualTo("nevermore");

            using var second = JsonDocument.Parse(lines[1]);
            await Assert.That(second.RootElement.GetProperty("outcome").GetString()).IsEqualTo("ConnectFailed");
            await Assert.That(second.RootElement.GetProperty("error").GetString()).IsEqualTo("ConnectionRefused");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task NeitherOutputStartsWithAByteOrderMark()
    {
        // Found by actually reading the files a run produced: Encoding.UTF8 emits a BOM, and a BOM at the
        // head of a JSON-lines file breaks the first record for every consumer that does not know to
        // strip it — Python's own json module among them. The Markdown grows a stray glyph before its
        // first heading. Both are only visible if somebody opens the file, so they get a test.
        var directory = Scratch();
        try
        {
            var observations = Path.Combine(directory, "observations.jsonl");
            using (var log = new ObservationLog(observations))
            {
                log.Append(new ProbeResult
                {
                    Host = Host("a.example.org"),
                    Outcome = CrawlOutcome.NoMssp,
                    ObservedAt = Now,
                });
            }

            var report = Path.Combine(directory, "report.md");
            CrawlReport.Write(report, new CrawlSummary
            {
                StartedAt = Now,
                FinishedAt = Now,
                StopReason = CrawlStopReason.Exhausted,
                Results = [],
                Hosts = [],
                Verdicts = new Dictionary<DiscoveryVerdict, int>(),
            }, new CrawlOptions());

            byte[] bom = [0xEF, 0xBB, 0xBF];
            await Assert.That(File.ReadAllBytes(observations).Take(3)).IsNotEquivalentTo(bom);
            await Assert.That(File.ReadAllBytes(report).Take(3)).IsNotEquivalentTo(bom);

            // And the record really does parse as JSON when read straight off the first byte.
            using var parsed = JsonDocument.Parse(File.ReadAllText(observations));
            await Assert.That(parsed.RootElement.GetProperty("host").GetString()).IsEqualTo("a.example.org");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task TheObservationLogIsAppendedToRatherThanReplaced()
    {
        var directory = Scratch();
        try
        {
            var path = Path.Combine(directory, "observations.jsonl");
            var result = new ProbeResult
            {
                Host = Host("a.example.org"),
                Outcome = CrawlOutcome.NoMssp,
                ObservedAt = Now,
            };

            using (var first = new ObservationLog(path))
            {
                first.Append(result);
            }

            using (var second = new ObservationLog(path))
            {
                second.Append(result);
            }

            await Assert.That(File.ReadAllLines(path).Length).IsEqualTo(2);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task TheReportSaysWhatHappenedAndWhy()
    {
        var data = MsspWire.Report(MsspWire.RepresentativeReport("peer.example.net 4000"));

        var summary = new CrawlSummary
        {
            StartedAt = Now,
            FinishedAt = Now + TimeSpan.FromMinutes(3),
            StopReason = CrawlStopReason.HostCap,
            Results =
            [
                new ProbeResult
                {
                    Host = Host("a.example.org"),
                    Outcome = CrawlOutcome.MsspReceived,
                    ObservedAt = Now,
                    Data = data,
                },
                new ProbeResult
                {
                    Host = Host("b.example.org"),
                    Outcome = CrawlOutcome.ConnectFailed,
                    ObservedAt = Now,
                    Error = "ConnectionRefused",
                },
            ],
            Hosts =
            [
                new HostRecord { Host = Host("a.example.org"), FirstSeen = Now, NotBefore = Now + TimeSpan.FromHours(24) },
                new HostRecord
                {
                    Host = Host("peer.example.net", 4000),
                    Depth = 1,
                    DiscoveredFrom = Host("a.example.org"),
                    FirstSeen = Now,
                },
            ],
            Verdicts = new Dictionary<DiscoveryVerdict, int>
            {
                [DiscoveryVerdict.Added] = 1,
                [DiscoveryVerdict.AlreadyKnown] = 4,
            },
        };

        var report = CrawlReport.Render(summary, new CrawlOptions());

        await Assert.That(report).Contains("the host cap was reached");
        await Assert.That(report).Contains("Corvid Nest");
        await Assert.That(report).Contains("peer.example.net:4000");
        await Assert.That(report).Contains("ConnectionRefused");
        await Assert.That(report).Contains("a cycle, or two servers naming the same peer");
        await Assert.That(report).Contains("2026-01-01 12:00:00Z");
    }

    [Test]
    public async Task AServerNameCannotBreakTheReportsTable()
    {
        // Every string in that table came off the wire from a stranger. A MUD called "Pipe|Dream" must
        // not be able to add a column to somebody's report.
        var data = MsspWire.Report(("NAME", ["Pipe|Dream\nSecond line"]));

        var summary = new CrawlSummary
        {
            StartedAt = Now,
            FinishedAt = Now,
            StopReason = CrawlStopReason.Exhausted,
            Results = [new ProbeResult { Host = Host("a.example.org"), Outcome = CrawlOutcome.MsspReceived, ObservedAt = Now, Data = data }],
            Hosts = [],
            Verdicts = new Dictionary<DiscoveryVerdict, int>(),
        };

        var row = CrawlReport.Render(summary, new CrawlOptions())
            .Split('\n')
            .Single(line => line.Contains("Pipe"));

        await Assert.That(row).Contains("Pipe\\|Dream");
        await Assert.That(row).DoesNotContain("\r");

        // Seven columns, so eight column separators — the one inside the name is escaped and does not
        // count, which is the whole point.
        var separators = row.Where((c, i) => c == '|' && (i == 0 || row[i - 1] != '\\')).Count();
        await Assert.That(separators).IsEqualTo(8);
    }
}
