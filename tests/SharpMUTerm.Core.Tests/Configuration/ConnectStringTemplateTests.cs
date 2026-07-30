using SharpMUTerm.Core.Configuration;

namespace SharpMUTerm.Core.Tests.Configuration;

/// <summary>
/// The login line as a template. These pin the four rules <see cref="ConnectStringTemplate"/> decides —
/// delimiters, case, escaping, unknown tokens — plus the two properties the whole design rests on: a
/// config that round-trips the <em>template</em> and forgets the <em>password</em>, and a connect string
/// with no tokens in it that comes back exactly as it went in.
/// </summary>
public class ConnectStringTemplateTests
{
    private static CharacterDefinition Character(string? connect = null, string? password = "hunter2") =>
        new() { Name = "Corvid", Password = password, ConnectString = connect };

    /// <summary>
    /// The default, and the one compatibility fact that matters: a character with no connect string of
    /// its own sends exactly the line the old hand-built interpolation sent. That is also the whole
    /// migration answer — null keeps meaning "the default", so no on-disk config needs touching.
    /// </summary>
    [Test]
    public async Task TheDefaultTemplateSendsWhatTheOldInterpolationSent()
    {
        await Assert.That(ConnectStringTemplate.Default).IsEqualTo("connect %CHARACTER% %PASSWORD%");
        await Assert.That(Character().ResolveConnectString()).IsEqualTo("connect Corvid hunter2");
        await Assert.That(Character(password: null).ResolveConnectString()).IsEqualTo("connect Corvid");
        await Assert.That(Character(password: string.Empty).ResolveConnectString()).IsEqualTo("connect Corvid");
    }

    /// <summary>Both tokens, substituted wherever they appear and however often.</summary>
    [Test]
    public async Task EachTokenIsSubstitutedEverywhereItAppears()
    {
        await Assert.That(Character("co %CHARACTER% %PASSWORD%").ResolveConnectString())
            .IsEqualTo("co Corvid hunter2");
        await Assert.That(Character("%PASSWORD%/%CHARACTER%").ResolveConnectString())
            .IsEqualTo("hunter2/Corvid");
        await Assert.That(Character("%CHARACTER% %CHARACTER%").ResolveConnectString())
            .IsEqualTo("Corvid Corvid");

        // An odd login syntax is the reason the tokens are placeholders rather than a fixed word order.
        await Assert.That(Character("connect \"%CHARACTER%\" %PASSWORD%").ResolveConnectString())
            .IsEqualTo("connect \"Corvid\" hunter2");
    }

    /// <summary>
    /// Token names are matched case-insensitively, like every other name this app resolves. The
    /// canonical spelling is upper case; a user who writes <c>%Password%</c> gets their password rather
    /// than a literal string sent to the server.
    /// </summary>
    [Test]
    public async Task TokenNamesAreMatchedWithoutRegardToCase()
    {
        foreach (var spelling in new[] { "%password%", "%Password%", "%PaSsWoRd%" })
        {
            await Assert.That(Character("connect %character% " + spelling).ResolveConnectString())
                .IsEqualTo("connect Corvid hunter2")
                .Because(spelling);
        }
    }

    /// <summary><c>%%</c> is a literal per cent, so a literal token can be written and sent.</summary>
    [Test]
    public async Task DoubledDelimitersAreALiteralPerCent()
    {
        await Assert.That(Character("say 100%% sure").ResolveConnectString()).IsEqualTo("say 100% sure");
        await Assert.That(Character("%%PASSWORD%%").ResolveConnectString()).IsEqualTo("%PASSWORD%");
        await Assert.That(Character("%%%CHARACTER%").ResolveConnectString()).IsEqualTo("%Corvid");
        await Assert.That(Character("%%%%").ResolveConnectString()).IsEqualTo("%%");
    }

    /// <summary>
    /// A name this resolver does not know is text, and so is a lone or unterminated delimiter. It is the
    /// conservative rule — only what is recognised is rewritten — and the visible one: the server refuses
    /// <c>%PASWORD%</c> and the reason is still legible in the line. Stripping it would send a login line
    /// silently missing its password, which on many servers logs you in as a guest instead of failing.
    /// </summary>
    [Test]
    public async Task AnUnknownOrUnterminatedTokenIsLeftExactlyAsTyped()
    {
        await Assert.That(Character("connect %CHARACTER% %PASWORD%").ResolveConnectString())
            .IsEqualTo("connect Corvid %PASWORD%");
        // Half-delimited, which is the spelling that comes of writing the tokens out from memory: the
        // first is not a token at all (its name would have to be "CHARACTER " with the space), so it is
        // copied through and only the well-formed one is substituted. One delimiter is not a token.
        await Assert.That(Character("connect %CHARACTER %PASSWORD%").ResolveConnectString())
            .IsEqualTo("connect %CHARACTER hunter2");
        await Assert.That(Character("50% off").ResolveConnectString()).IsEqualTo("50% off");
        await Assert.That(Character("%").ResolveConnectString()).IsEqualTo("%");
        await Assert.That(Character("%CHARACTER").ResolveConnectString()).IsEqualTo("%CHARACTER");
    }

    /// <summary>
    /// An empty value takes one adjacent space with it, so the default template with no password sends
    /// no dangling space — the behaviour the old hand-built line had, kept. Nothing else is collapsed and
    /// the line is not trimmed.
    /// </summary>
    [Test]
    public async Task AnEmptyValueTakesOneAdjacentSpaceWithIt()
    {
        await Assert.That(Character("connect %CHARACTER% %PASSWORD%", null).ResolveConnectString())
            .IsEqualTo("connect Corvid");

        // No space before it: the one after goes instead.
        await Assert.That(Character("%PASSWORD% connect %CHARACTER%", null).ResolveConnectString())
            .IsEqualTo("connect Corvid");

        // No space either side: nothing to give up.
        await Assert.That(Character("connect %CHARACTER%%PASSWORD%", null).ResolveConnectString())
            .IsEqualTo("connect Corvid");

        // Exactly one space, so a deliberate double stays a double — the rule is narrow on purpose.
        await Assert.That(Character("connect %CHARACTER%  %PASSWORD%", null).ResolveConnectString())
            .IsEqualTo("connect Corvid ");

        // And a template that resolves to nothing at all resolves to nothing, rather than to a space.
        await Assert.That(Character("%PASSWORD%", null).ResolveConnectString()).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// A substituted value is never rescanned, so a password may contain anything — including the
    /// delimiter, including a token's own spelling. This is the property that makes substitution safe to
    /// do with a secret: nothing the user's password says can be read as syntax.
    /// </summary>
    [Test]
    public async Task ASubstitutedValueIsNeverReinterpreted()
    {
        await Assert.That(Character(password: "50%off").ResolveConnectString())
            .IsEqualTo("connect Corvid 50%off");
        await Assert.That(Character(password: "%PASSWORD%").ResolveConnectString())
            .IsEqualTo("connect Corvid %PASSWORD%");
        await Assert.That(Character(password: "%%").ResolveConnectString()).IsEqualTo("connect Corvid %%");
        await Assert.That(Character(password: "p@ss w:rd\"[]$*").ResolveConnectString())
            .IsEqualTo("connect Corvid p@ss w:rd\"[]$*");

        // The name is not rescanned either, however unwisely someone has named a character.
        var awkward = new CharacterDefinition { Name = "%PASSWORD%", Password = "hunter2" };
        await Assert.That(awkward.ResolveConnectString()).IsEqualTo("connect %PASSWORD% hunter2");
    }

    /// <summary>
    /// The compatibility promise: an existing config's connect string, which by definition has no tokens
    /// in it, is passed through untouched. The one rewrite it can see is the <c>%%</c> escape, which is
    /// the unavoidable cost of having an escape at all — and a bare <c>%</c> is not affected by it.
    /// </summary>
    [Test]
    public async Task AConnectStringWithNoTokensPassesThroughUntouched()
    {
        foreach (var line in new[]
        {
            "connect Corvid hunter2",
            "co guest guest",
            "CONNECT Corvid",
            "connect Corvid  hunter2 ",
            "  leading and trailing  ",
            "connect \"Two Names\" pass",
            "1 100% ok $ * [] {} \\ 'quoted'",
        })
        {
            await Assert.That(Character(line).ResolveConnectString()).IsEqualTo(line).Because(line);
        }
    }

    /// <summary>A blank connect string is "unset", so it falls back to the default rather than sending air.</summary>
    [Test]
    public async Task ABlankConnectStringFallsBackToTheDefault()
    {
        foreach (var blank in new[] { null, string.Empty, "   ", "\t" })
        {
            await Assert.That(Character(blank).ResolveConnectString())
                .IsEqualTo("connect Corvid hunter2")
                .Because(blank is null ? "null" : $"'{blank}'");
        }
    }

    /// <summary>
    /// A save/reload keeps the template <em>and</em> the password, and the reloaded pair resolves to a working
    /// login line without anybody retyping anything. That last clause is the whole feature.
    /// <para>
    /// <b>Two assertions here are inverted from what this test used to claim.</b> It was
    /// <c>AConfigRoundTripsTheTemplateAndForgetsThePassword</c>, and it required the password to come back
    /// null — the property of a field the client did not save. Passwords are saved now, so "forgets the
    /// password" is the bug rather than the contract, and the pinned property is the opposite one.
    /// </para>
    /// <para>
    /// What is <em>not</em> inverted is where the secret goes. It is checked against the whole pair of files
    /// through <see cref="ConfigurationStore.Save"/>, not through <c>Serialize</c>, precisely because the
    /// config document must still contain no password — see
    /// <c>PasswordAtRestTests.ConfigurationStoreDoesNotWriteThePasswordIntoTheConfigDocument</c>. That is why
    /// the tokens survive a change that removed their original reason for existing: the config can hold
    /// <c>co %CHARACTER% %PASSWORD%</c> and be safe to paste, the secret stays in one masked field instead of
    /// copied into a connect line drawn in the clear, and substitution still happens on the way to the socket
    /// so the resolved line reaches no echo, no transcript and no history entry.
    /// </para>
    /// </summary>
    [Test]
    public async Task AConfigRoundTripsBothTheTemplateAndThePassword()
    {
        var config = new AppConfiguration
        {
            Worlds =
            {
                new WorldDefinition
                {
                    Name = "Aetherfall",
                    Host = "aetherfall.mux",
                    Characters =
                    {
                        new CharacterDefinition
                        {
                            Name = "Corvid",
                            Password = "hunter2",
                            ConnectString = "co %CHARACTER% %PASSWORD%",
                            AutoLogin = true,
                        },
                    },
                },
            },
        };

        var directory = Path.Combine(Path.GetTempPath(), $"smuterm-template-{Guid.NewGuid():N}");
        var configPath = Path.Combine(directory, "config.json");
        try
        {
            ConfigurationStore.Save(configPath, config);

            // The config document holds the template and no secret; the secret is in the file beside it.
            var document = File.ReadAllText(configPath);
            await Assert.That(document).Contains("co %CHARACTER% %PASSWORD%");
            await Assert.That(document).DoesNotContain("hunter2");
            await Assert.That(File.ReadAllText(SecretsStore.PathFor(configPath))).Contains("hunter2");

            var reloaded = ConfigurationStore.Load(configPath).Worlds[0].Characters[0];
            await Assert.That(reloaded.Password).IsEqualTo("hunter2");
            await Assert.That(reloaded.ConnectString).IsEqualTo("co %CHARACTER% %PASSWORD%");
            await Assert.That(reloaded.AutoLogin).IsTrue();

            // Straight off a reload, with nothing typed: the working login line.
            await Assert.That(reloaded.ResolveConnectString()).IsEqualTo("co Corvid hunter2");

            // And clearing the field is still how a stored credential is forgotten — the token drops itself
            // and the space that was holding it apart, so the line stays valid rather than trailing
            // whitespace.
            reloaded.Password = null;
            await Assert.That(reloaded.ResolveConnectString()).IsEqualTo("co Corvid");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            {
                // Nothing a test should fail over.
            }
        }
    }

    /// <summary>
    /// Nothing was migrated, and nothing needed to be: a v2 document with no <c>connectString</c> at all
    /// still resolves to the line it always did, because null means "the default" both before and after.
    /// </summary>
    [Test]
    public async Task AnExistingConfigNeedsNoMigrationForTheNewDefault()
    {
        const string stored = """
        {
          "version": 2,
          "worlds": [
            {
              "name": "Aetherfall",
              "host": "aetherfall.mux",
              "characters": [ { "name": "Corvid", "autoLogin": true } ]
            }
          ]
        }
        """;

        var config = ConfigurationStore.Deserialize(stored);
        var character = config.Worlds[0].Characters[0];

        await Assert.That(config.Version).IsEqualTo(AppConfiguration.CurrentVersion);
        await Assert.That(character.ConnectString).IsNull();
        await Assert.That(character.ResolveConnectString()).IsEqualTo("connect Corvid");

        character.Password = "hunter2";
        await Assert.That(character.ResolveConnectString()).IsEqualTo("connect Corvid hunter2");
    }
}
