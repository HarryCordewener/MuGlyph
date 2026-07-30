using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Telnet;

namespace SharpMUTerm.Core.Tests.Configuration;

/// <summary>
/// v3 changed what a world's <c>encoding</c> <em>means</em>: it was a preference the client stated to
/// CHARSET (and then ignored when decoding), and it is now an override of the negotiated result, with
/// <c>auto</c> as the default.
/// <para>
/// A rename of meaning is exactly what the schema version exists for, so this is a migration step and
/// not a load-time shim: the document is rewritten once, at <see cref="AppConfiguration.CurrentVersion"/>,
/// and everything downstream reads one shape. A dual-read path — "treat a stored UTF-8 as auto" — would
/// have left every later reader unable to tell a migrated world from one somebody deliberately pinned
/// to UTF-8, for ever.
/// </para>
/// <para>
/// The explicit <c>UTF-8</c> every v2 document carries becomes <c>auto</c>. Nobody chose it: it was the
/// property's default, written out on every save. Keeping it would pin every existing world to an
/// override and defeat the feature for exactly the people who already have a config.
/// </para>
/// </summary>
public class EncodingMigrationTests
{
    /// <summary>
    /// The shape a v2 config actually has on disk — every world carrying the old default, and one
    /// carrying a genuine choice. The first two become <c>auto</c>; the third is left alone, because
    /// Latin-1 in a v2 file was typed on the F5 screen by somebody who meant it.
    /// </summary>
    private const string V2Document = """
        {
          "version": 2,
          "worlds": [
            { "name": "Aetherfall", "host": "aetherfall.mux", "port": 4201, "encoding": "UTF-8" },
            { "name": "Convergence MUSH", "host": "convergence.mux", "port": 6250, "encoding": "utf-8" },
            { "name": "Grapevine", "host": "grapevine.haus", "port": 4000, "encoding": "ISO-8859-1" }
          ],
          "triggerSets": []
        }
        """;

    [Test]
    public async Task V2_ExplicitUtf8_BecomesAuto_AndAGenuineChoiceIsKept()
    {
        var config = ConfigurationStore.Deserialize(V2Document);

        await Assert.That(config.Version).IsEqualTo(AppConfiguration.CurrentVersion);
        await Assert.That(config.Worlds[0].Encoding).IsEqualTo("auto");
        await Assert.That(config.Worlds[1].Encoding).IsEqualTo("auto"); // case-insensitively, too
        await Assert.That(config.Worlds[2].Encoding).IsEqualTo("ISO-8859-1");

        // And the migrated worlds mean it: auto states a preference and overrides nothing.
        await Assert.That(TelnetSessionOptions.ResolveEncoding(config.Worlds[0].Encoding)).IsNull();
        await Assert.That(TelnetSessionOptions.ResolveEncoding(config.Worlds[2].Encoding)).IsNotNull();
    }

    /// <summary>Nothing else about the worlds moved — this step touches one field and no others.</summary>
    [Test]
    public async Task V2_MigrationTouchesNothingButTheEncoding()
    {
        var config = ConfigurationStore.Deserialize(V2Document);

        await Assert.That(config.Worlds.Count).IsEqualTo(3);
        await Assert.That(config.Worlds[0].Host).IsEqualTo("aetherfall.mux");
        await Assert.That(config.Worlds[0].Port).IsEqualTo(4201);
        await Assert.That(config.Worlds[2].Host).IsEqualTo("grapevine.haus");
    }

    /// <summary>
    /// A v3 document written by this build survives a round trip through it unchanged — the migration
    /// runs once and is not a filter that keeps re-deciding. In particular a world deliberately pinned
    /// to UTF-8 <em>after</em> the migration stays pinned to UTF-8: only the v2 step rewrites it.
    /// </summary>
    [Test]
    public async Task V3_RoundTripsUnchanged_IncludingAWorldDeliberatelyPinnedToUtf8()
    {
        var config = new AppConfiguration
        {
            Worlds =
            {
                new WorldDefinition { Name = "Auto", Host = "a", Port = 1 },
                new WorldDefinition { Name = "Pinned", Host = "b", Port = 2, Encoding = "UTF-8" },
                new WorldDefinition { Name = "Latin", Host = "c", Port = 3, Encoding = "ISO-8859-1" },
            },
        };

        var once = ConfigurationStore.Deserialize(ConfigurationStore.Serialize(config));
        var twice = ConfigurationStore.Deserialize(ConfigurationStore.Serialize(once));

        await Assert.That(once.Worlds[0].Encoding).IsEqualTo("auto");
        await Assert.That(once.Worlds[1].Encoding).IsEqualTo("UTF-8");
        await Assert.That(once.Worlds[2].Encoding).IsEqualTo("ISO-8859-1");
        await Assert.That(twice.Worlds.Select(w => w.Encoding))
            .IsEquivalentTo(once.Worlds.Select(w => w.Encoding));
    }

    /// <summary>A brand-new world follows negotiation; that is the default the whole change exists for.</summary>
    [Test]
    public async Task ANewWorldDefaultsToAuto()
    {
        await Assert.That(new WorldDefinition().Encoding).IsEqualTo("auto");
        await Assert.That(TelnetSessionOptions.ResolveEncoding(new WorldDefinition().Encoding)).IsNull();
    }

    /// <summary>
    /// A v1 document runs both steps: its automation is lifted onto a character (v2) <em>and</em> its
    /// encoding is freed (v3). The steps are cumulative, not exclusive.
    /// </summary>
    [Test]
    public async Task V1_RunsBothStepsInOrder()
    {
        var config = ConfigurationStore.Deserialize(
            """
            {
              "version": 1,
              "worlds": [
                {
                  "name": "Old World",
                  "host": "old.example.net",
                  "encoding": "UTF-8",
                  "triggers": [ { "name": "hail", "pattern": "waves" } ]
                }
              ]
            }
            """);

        await Assert.That(config.Version).IsEqualTo(AppConfiguration.CurrentVersion);
        await Assert.That(config.Worlds[0].Encoding).IsEqualTo("auto");
        await Assert.That(config.Worlds[0].Characters).HasSingleItem();
        await Assert.That(config.TriggerSets[0].Triggers[0].Pattern).IsEqualTo("waves");
    }

    /// <summary>
    /// A v2 world that named no encoding at all deserializes to the new default rather than to the old
    /// one. There is nothing for the migrator to rewrite, and nothing that should be.
    /// </summary>
    [Test]
    public async Task V2_WorldWithNoEncodingKey_LandsOnAuto()
    {
        var config = ConfigurationStore.Deserialize(
            """{"version":2,"worlds":[{"name":"Bare","host":"h","port":1}],"triggerSets":[]}""");

        await Assert.That(config.Worlds[0].Encoding).IsEqualTo("auto");
    }
}
