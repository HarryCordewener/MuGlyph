using SharpMUTerm.Crawler;

namespace SharpMUTerm.Crawler.Tests;

/// <summary>
/// Where a run's starting hosts come from, and — more importantly — what it refuses to read.
/// </summary>
public class SeedTests
{
    private static string WriteTemp(string name, string content)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sharpmuterm-crawler-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Test]
    public async Task ASeedFileIsHostSpacePortWithCommentsIgnored()
    {
        var path = WriteTemp("seeds.txt", """
            # The hosts this run starts from.
            mud.example.org 4201
            other.example.net 4000    # trailing comment

            2001:db8::1 4201
            legacy.example.com:23
            """);

        try
        {
            var seeds = Seeds.FromFile(path);

            await Assert.That(seeds.Rejected).IsEmpty();
            await Assert.That(seeds.Hosts.Select(h => h.ToReferralString())).IsEquivalentTo(new[]
            {
                "mud.example.org 4201",
                "other.example.net 4000",
                "2001:db8::1 4201",
                "legacy.example.com 23",
            });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public async Task ABadSeedLineIsReportedRatherThanStoppingTheRun()
    {
        var path = WriteTemp("seeds.txt", """
            good.example.org 4201
            this is not a host
            good.example.org 4201
            """);

        try
        {
            var seeds = Seeds.FromFile(path);

            await Assert.That(seeds.Hosts.Count).IsEqualTo(1); // the duplicate collapses
            await Assert.That(seeds.Rejected).IsEquivalentTo(new[] { "this is not a host" });
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    public async Task AWorldsFileYieldsOnlyHostsAndPortsAndNothingElse()
    {
        // A realistic SharpMUTerm configuration, including the fields this tool must never look at.
        var path = WriteTemp("config.json", """
            {
              "version": 3,
              "worlds": [
                {
                  "name": "Corvid",
                  "host": "corvid.example.org",
                  "port": 4201,
                  "useTls": false,
                  "characters": [
                    { "name": "Ann", "login": "connect Ann hunter2", "passwordRef": "9f2a-…" }
                  ]
                },
                {
                  "name": "Second",
                  "host": "second.example.net",
                  "port": 2000,
                  "characters": []
                },
                {
                  "name": "No port",
                  "host": "broken.example.org"
                }
              ]
            }
            """);

        try
        {
            var seeds = Seeds.FromWorldsFile(path);

            await Assert.That(seeds.Hosts.Select(h => h.ToReferralString()))
                .IsEquivalentTo(new[] { "corvid.example.org 4201", "second.example.net 2000" });

            // The world with no port is reported by name; nothing about its characters is read at all.
            await Assert.That(seeds.Rejected).IsEquivalentTo(new[] { "No port: no host or port" });
            await Assert.That(string.Join("|", seeds.Rejected)).DoesNotContain("hunter2");
            await Assert.That(string.Join("|", seeds.Rejected)).DoesNotContain("Ann");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Test]
    [Arguments("GetFolderPath")]
    [Arguments("SpecialFolder")]
    [Arguments("XDG_CONFIG_HOME")]
    [Arguments("secrets.json")]
    public async Task TheCrawlerCannotReachForTheUsersConfigurationDirectory(string forbidden)
    {
        // The guarantee this tool makes is that it reads nothing it was not explicitly pointed at, and
        // that guarantee is worth a test that cannot be satisfied by a careful reading. Every method a
        // program calls leaves its name in the assembly's metadata, and every string literal it holds is
        // in there too — so if a future change ever resolves the configuration directory, or names the
        // secrets file, the name is in this file and this fails.
        var bytes = await File.ReadAllBytesAsync(typeof(Seeds).Assembly.Location);
        var needle = System.Text.Encoding.UTF8.GetBytes(forbidden);

        var found = false;
        for (var i = 0; i + needle.Length <= bytes.Length && !found; i++)
        {
            found = bytes.AsSpan(i, needle.Length).SequenceEqual(needle);
        }

        await Assert.That(found).IsFalse()
            .Because($"the crawler assembly refers to \"{forbidden}\"; it must not know where the user's "
                + "configuration lives, only what it was handed on the command line");
    }

    // ---- The command line ----

    [Test]
    public async Task ASeedIsTakenFromTheCommandLine()
    {
        var parsed = CommandLine.Parse(["--seed", "mud.example.org:4201", "--seed", "other.example.net 23"]);

        await Assert.That(parsed.Error).IsNull();
        await Assert.That(parsed.Options!.Seeds.Select(h => h.ToReferralString()))
            .IsEquivalentTo(new[] { "mud.example.org 4201", "other.example.net 23" });
    }

    [Test]
    public async Task PolitenessSettingsCanBeLoosenedOnlyDeliberately()
    {
        var defaults = CommandLine.Parse(["--seed", "a.example.org:1"]).Options!;
        await Assert.That(defaults.MaxConcurrency).IsEqualTo(4);
        await Assert.That(defaults.RevisitInterval).IsEqualTo(TimeSpan.FromHours(24));

        var loosened = CommandLine.Parse(
            ["--seed", "a.example.org:1", "--concurrency", "2", "--revisit", "48", "--max-hosts", "10"]).Options!;
        await Assert.That(loosened.MaxConcurrency).IsEqualTo(2);
        await Assert.That(loosened.RevisitInterval).IsEqualTo(TimeSpan.FromHours(48));
        await Assert.That(loosened.MaxHosts).IsEqualTo(10);
    }

    [Test]
    public async Task ASettingThatCouldOnlyBeATypoIsRefusedBeforeAnythingReachesTheNetwork()
    {
        await Assert.That(CommandLine.Parse(["--seed", "a.example.org:1", "--concurrency", "0"]).Error).IsNotNull();
        await Assert.That(CommandLine.Parse(["--seed", "a.example.org:1", "--max-hosts", "0"]).Error).IsNotNull();
        await Assert.That(CommandLine.Parse(["--nonsense"]).Error).IsNotNull();
        await Assert.That(CommandLine.Parse(["--seed", "not a host at all"]).Error).IsNotNull();
    }

    [Test]
    public async Task TheHelpTextSaysWhatTheToolWillAndWillNotDo()
    {
        // A server operator who finds this in their logs and searches for the name should reach a tool
        // that states plainly that it never logs in.
        await Assert.That(CommandLine.Parse(["--help"]).WantsHelp).IsTrue();
        await Assert.That(CommandLine.Usage).Contains("never logs in and never sends a command");
        await Assert.That(CommandLine.Usage).Contains("SHARPMUTERM-MSSPCRAWLER");
    }
}
