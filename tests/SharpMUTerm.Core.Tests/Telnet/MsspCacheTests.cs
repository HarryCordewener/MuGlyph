using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Telnet.Mssp;

namespace SharpMUTerm.Core.Tests.Telnet;

/// <summary>
/// What the INFO screen reads: which endpoint a report is filed under, how the three states are told
/// apart, what a second connection does to the first one's report, and what a hostile server gets to
/// put on disk.
/// <para>
/// Nothing here touches <c>ConfigurationStore.DefaultPath</c>. Every case either uses a memory-only
/// cache or a temporary directory of its own — the developer's own configuration is not a fixture.
/// </para>
/// </summary>
public class MsspCacheTests
{
    private static readonly DateTimeOffset Noon = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    private static MsspData Report(params (string Variable, string[] Values)[] entries) =>
        MsspWire.Report(entries);

    /// <summary>A directory of its own, deleted afterwards; never created until something writes.</summary>
    private sealed class TempRoot : IDisposable
    {
        public TempRoot() =>
            Root = Path.Combine(Path.GetTempPath(), $"smuterm-mssp-{Guid.NewGuid():N}");

        public string Root { get; }

        /// <summary>One level deeper, so the store has a folder to create the way the real one does.</summary>
        public string ConfigPath => Path.Combine(Root, "SharpMUTerm", "config.json");

        public string CachePath => MsspCache.PathFor(ConfigPath);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A temp directory that will not go is not this test's business.
            }
        }
    }

    // ---- Identity ----

    [Test]
    public async Task ACacheFileSitsBesideTheConfigurationAndItsSecrets()
    {
        var config = Path.Combine(Path.GetTempPath(), "nowhere", "SharpMUTerm", "config.json");

        await Assert.That(MsspCache.PathFor(config))
            .IsEqualTo(Path.Combine(Path.GetTempPath(), "nowhere", "SharpMUTerm", MsspCache.FileName));
        await Assert.That(Path.GetDirectoryName(MsspCache.PathFor(config)))
            .IsEqualTo(Path.GetDirectoryName(SecretsStore.PathFor(config)));
    }

    [Test]
    public async Task OneServerIsOneEntryHoweverItsHostIsSpelled()
    {
        // The folding is what stops a server accumulating a half-filled entry per spelling its worlds
        // happen to use. A trailing root dot and a capital are the same host to DNS and must be here.
        var cache = new MsspCache();
        cache.RecordReport("MUD.Example.ORG.", 4201, Report(("NAME", ["One"])), Noon);
        cache.RecordReport("mud.example.org", 4201, Report(("NAME", ["Two"])), Noon);

        await Assert.That(cache.All).HasCount().EqualTo(1);
        await Assert.That(cache.Find("MUD.EXAMPLE.ORG", 4201)!.Report!.Name).IsEqualTo("Two");
    }

    [Test]
    public async Task TwoPortsOnOneHostAreTwoServers()
    {
        // The plaintext port and the TLS port are two endpoints and may run two games. Keying by host
        // alone would have one report standing for both, which is the one reading a user cannot correct.
        var cache = new MsspCache();
        cache.RecordReport("mud.example.org", 4000, Report(("NAME", ["Plain"])), Noon);
        cache.RecordReport("mud.example.org", 4001, Report(("NAME", ["Secure"])), Noon);

        await Assert.That(cache.Find("mud.example.org", 4000)!.Report!.Name).IsEqualTo("Plain");
        await Assert.That(cache.Find("mud.example.org", 4001)!.Report!.Name).IsEqualTo("Secure");
    }

    // ---- The three states ----

    [Test]
    public async Task AnEndpointNothingHasReachedHasNoEntryAtAll()
    {
        await Assert.That(new MsspCache().Find("never.example.org", 4000)).IsNull();
    }

    [Test]
    public async Task AConnectionWithNoReportIsRecordedAsExactlyThat()
    {
        // The state the whole two-timestamp design exists for: we spoke to this server and it published
        // nothing. It must be distinguishable from never having spoken to it, and it is — there is an
        // observation, and its report is null.
        var cache = new MsspCache();
        cache.RecordConnection("quiet.example.org", 4000, Noon);

        var observation = cache.Find("quiet.example.org", 4000);
        await Assert.That(observation).IsNotNull();
        await Assert.That(observation!.PublishesNothing).IsTrue();
        await Assert.That(observation.ObservedAt).IsNull();
        await Assert.That(observation.ConnectedAt).IsEqualTo(Noon);
    }

    [Test]
    public async Task AReportKeepsTheConnectionTimeItArrivedUnder()
    {
        var cache = new MsspCache();
        cache.RecordConnection("mud.example.org", 4000, Noon);
        cache.RecordReport("mud.example.org", 4000, Report(("NAME", ["Corvid"])), Noon.AddSeconds(2));

        var observation = cache.Find("mud.example.org", 4000)!;
        await Assert.That(observation.ConnectedAt).IsEqualTo(Noon);
        await Assert.That(observation.ObservedAt).IsEqualTo(Noon.AddSeconds(2));
        await Assert.That(observation.PublishesNothing).IsFalse();
    }

    [Test]
    public async Task AServerThatStopsPublishingKeepsTheReportItLastGave()
    {
        // Its date moves on and the report's does not, which is the point: the screen dates the report,
        // so a stale player count is labelled stale rather than blanked or re-dated. Clearing it on the
        // next connect would throw away the only answer we have in order to be freshly ignorant.
        var cache = new MsspCache();
        cache.RecordReport("mud.example.org", 4000, Report(("PLAYERS", ["17"])), Noon);
        cache.RecordConnection("mud.example.org", 4000, Noon.AddDays(30));

        var observation = cache.Find("mud.example.org", 4000)!;
        await Assert.That(observation.Report!.Players).IsEqualTo(17);
        await Assert.That(observation.ObservedAt).IsEqualTo(Noon);
        await Assert.That(observation.ConnectedAt).IsEqualTo(Noon.AddDays(30));
    }

    // ---- A second report ----

    [Test]
    public async Task ASecondReportReplacesTheFirstRatherThanMergingWithIt()
    {
        // MSSP is not a delta protocol: a server sends its whole table once per connection. A merge
        // would keep a variable it has stopped publishing for ever — the accumulating-room-exits failure
        // in a different costume — and would leave a report that is a snapshot of no moment that existed.
        var cache = new MsspCache();
        cache.RecordReport(
            "mud.example.org", 4000, Report(("NAME", ["Old"]), ("DISCORD", ["https://old"])), Noon);
        cache.RecordReport("mud.example.org", 4000, Report(("NAME", ["New"])), Noon.AddDays(1));

        var report = cache.Find("mud.example.org", 4000)!.Report!;
        await Assert.That(report.Name).IsEqualTo("New");
        await Assert.That(report.ContainsKey("DISCORD")).IsFalse();
        await Assert.That(cache.Find("mud.example.org", 4000)!.ObservedAt).IsEqualTo(Noon.AddDays(1));
    }

    [Test]
    public async Task AnEmptyReportIsStillAReportAndStillReplaces()
    {
        // A server that negotiates MSSP and sends no variables has said something: it publishes, and it
        // publishes nothing in particular. That is not the same as never having answered.
        var cache = new MsspCache();
        cache.RecordReport("mud.example.org", 4000, Report(("NAME", ["Was"])), Noon);
        cache.RecordReport("mud.example.org", 4000, MsspData.Empty, Noon.AddDays(1));

        var observation = cache.Find("mud.example.org", 4000)!;
        await Assert.That(observation.PublishesNothing).IsFalse();
        await Assert.That(observation.Report!.Count).IsEqualTo(0);
    }

    // ---- Bounds ----

    [Test]
    public async Task AHostileReportIsCutDownBeforeItIsStored()
    {
        var cache = new MsspCache();
        var flood = Enumerable.Range(0, MsspCache.MaxVariables + 50)
            .Select(i => ($"VAR{i}", new[] { new string('x', MsspCache.MaxValueLength * 4) }))
            .Append(("PORT", Enumerable.Range(0, MsspCache.MaxValuesPerVariable + 20)
                .Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .ToArray()))
            .ToArray();

        cache.RecordReport("hostile.example.org", 4000, Report(flood), Noon);
        var report = cache.Find("hostile.example.org", 4000)!.Report!;

        await Assert.That(report.Count).IsEqualTo(MsspCache.MaxVariables);
        await Assert.That(report["VAR0"][0].Length).IsEqualTo(MsspCache.MaxValueLength);
        // PORT was appended past the variable cap, so it is not kept at all — the head of what the
        // server sent survives, which is a subset a reader can reason about.
        await Assert.That(report.ContainsKey("PORT")).IsFalse();
    }

    [Test]
    public async Task AMultiValuedVariableInsideTheCapKeepsEveryValueInOrder()
    {
        var cache = new MsspCache();
        cache.RecordReport("mud.example.org", 4000, Report(("PORT", ["80", "23", "4201"])), Noon);

        await Assert.That(cache.Find("mud.example.org", 4000)!.Report!["PORT"])
            .IsEquivalentTo(new[] { "80", "23", "4201" });
    }

    [Test]
    public async Task TheOldestEndpointsAreDroppedOnceTheFileIsFull()
    {
        var cache = new MsspCache();
        for (var i = 0; i < MsspCache.MaxEndpoints + 10; i++)
        {
            cache.RecordConnection($"host{i}.example.org", 4000, Noon.AddMinutes(i));
        }

        await Assert.That(cache.All).HasCount().EqualTo(MsspCache.MaxEndpoints);
        await Assert.That(cache.Find("host0.example.org", 4000)).IsNull();
        await Assert.That(cache.Find($"host{MsspCache.MaxEndpoints + 9}.example.org", 4000)).IsNotNull();
    }

    // ---- Disk ----

    [Test]
    public async Task ACacheWithNoPathWritesNothingAnywhere()
    {
        // This is the guarantee a snapshot and every test in the suite runs on, and it is structural:
        // there is no file to write to, rather than a check at each use site that could be forgotten.
        using var temp = new TempRoot();
        var cache = new MsspCache();
        cache.RecordReport("mud.example.org", 4000, Report(("NAME", ["Corvid"])), Noon);

        await Assert.That(cache.IsPersistent).IsFalse();
        await Assert.That(Directory.Exists(temp.Root)).IsFalse();
    }

    [Test]
    public async Task AReportSurvivesARestartWithItsValuesAndTheirOrderIntact()
    {
        using var temp = new TempRoot();
        var writer = new MsspCache(temp.CachePath);
        writer.RecordConnection("mud.example.org", 4201, Noon);
        writer.RecordReport(
            "mud.example.org",
            4201,
            Report(("NAME", ["Corvid Nest"]), ("PORT", ["80", "23", "4201"]), ("VANITY", ["least", "most"])),
            Noon.AddSeconds(1));

        var reread = new MsspCache(temp.CachePath);
        var observation = reread.Find("mud.example.org", 4201)!;

        await Assert.That(reread.Problem).IsNull();
        await Assert.That(observation.ConnectedAt).IsEqualTo(Noon);
        await Assert.That(observation.ObservedAt).IsEqualTo(Noon.AddSeconds(1));
        await Assert.That(observation.Report!.Name).IsEqualTo("Corvid Nest");
        await Assert.That(observation.Report["PORT"]).IsEquivalentTo(new[] { "80", "23", "4201" });

        // Order is meaning in MSSP — "least to most relevant" — so the on-disk form is an array and the
        // wire order has to come back the way it went in, not however a JSON object happened to keep it.
        await Assert.That(observation.Report.Keys).IsEquivalentTo(new[] { "NAME", "PORT", "VANITY" });
    }

    [Test]
    public async Task AConnectionWithNoReportSurvivesARestartAsThatStateAndNotAsNothing()
    {
        using var temp = new TempRoot();
        new MsspCache(temp.CachePath).RecordConnection("quiet.example.org", 4000, Noon);

        var observation = new MsspCache(temp.CachePath).Find("quiet.example.org", 4000);
        await Assert.That(observation).IsNotNull();
        await Assert.That(observation!.PublishesNothing).IsTrue();
    }

    [Test]
    public async Task AnUnreadableCacheStartsEmptyAndSaysSoInsteadOfThrowing()
    {
        using var temp = new TempRoot();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.CachePath)!);
        File.WriteAllText(temp.CachePath, "{ this is not json");

        var cache = new MsspCache(temp.CachePath);
        await Assert.That(cache.All).IsEmpty();
        await Assert.That(cache.Problem).IsNotNull();
    }

    [Test]
    public async Task ACacheFromANewerSchemaIsIgnoredRatherThanMisread()
    {
        using var temp = new TempRoot();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.CachePath)!);
        File.WriteAllText(temp.CachePath, """{ "version": 99, "servers": { "a:1": {} } }""");

        var cache = new MsspCache(temp.CachePath);
        await Assert.That(cache.All).IsEmpty();
        await Assert.That(cache.Problem).IsNotNull();
    }

    [Test]
    public async Task OneUnreadableEntryDoesNotCostTheRest()
    {
        using var temp = new TempRoot();
        var writer = new MsspCache(temp.CachePath);
        writer.RecordConnection("good.example.org", 4000, Noon);

        var text = File.ReadAllText(temp.CachePath)
            .Replace("\"servers\": {", "\"servers\": {\n    \"bad:1\": { \"connectedAt\": \"not a date\" },");
        File.WriteAllText(temp.CachePath, text);

        var cache = new MsspCache(temp.CachePath);
        await Assert.That(cache.Find("good.example.org", 4000)).IsNotNull();
        await Assert.That(cache.Problem).IsNotNull();
    }
}
