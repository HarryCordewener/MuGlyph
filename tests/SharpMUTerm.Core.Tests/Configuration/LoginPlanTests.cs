using System.Text.Json;
using System.Text.Json.Nodes;
using SharpMUTerm.Core.Configuration;

namespace SharpMUTerm.Core.Tests.Configuration;

/// <summary>
/// The rule that replaced <c>autoLogin</c>: <b>a character logs itself in when its configuration says
/// what to send</b> — a saved password, or a connect line of its own — and there is no separate switch
/// that can disagree with either.
/// <para>
/// The flag it replaced was a trap and then, worse, an unreachable one. Sending the connect line
/// required it <em>as well as</em> a password, so a character with a credential saved and the flag at
/// its default connected and typed nothing; and the flag could not be turned on, because the F5 form
/// drew it as a readout of a checkbox the character list never rendered. The reporter's own
/// configuration had two characters in exactly that state. What is pinned here is the replacement, in
/// all four of its answers, plus the migration that gets an existing document to them.
/// </para>
/// </summary>
public class LoginPlanTests
{
    private static CharacterDefinition Character(string? password = null, string? connect = null) =>
        new() { Name = "Corvid", Password = password, ConnectString = connect };

    // ---- The rule ---------------------------------------------------------------------------------

    /// <summary>
    /// <b>The headline.</b> A saved password, and nothing else said anywhere, sends the default template
    /// with the password in it. This is the reporter's configuration, and it used to resolve to nothing.
    /// </summary>
    [Test]
    public async Task ASavedPasswordAloneIsTheInstructionToLogIn()
    {
        var character = Character(password: "hunter2");

        await Assert.That(character.Login()).IsEqualTo(LoginPlan.WithPassword);
        await Assert.That(character.ResolveConnectString()).IsEqualTo("connect Corvid hunter2");
    }

    /// <summary>
    /// The deliberate opt-out, and the only one there is: fill in neither field. It has to be the silent
    /// state — a client that announced "no login configured" on every connect would be nagging about a
    /// choice — which is why the *inert* case below is a separate answer rather than folded in here.
    /// </summary>
    [Test]
    public async Task NoPasswordAndNoConnectLineSendsNothing()
    {
        await Assert.That(Character().Login()).IsEqualTo(LoginPlan.Nothing);
    }

    /// <summary>
    /// A connect line of the character's own counts as intent even with no password — a passwordless
    /// world, or a login prompt that answers to something other than <c>connect</c>. "Send only when a
    /// password exists" would have made those unconfigurable.
    /// </summary>
    [Test]
    public async Task AConnectLineOfItsOwnCountsWithoutAPassword()
    {
        var character = Character(connect: "connect %CHARACTER%");

        await Assert.That(character.Login()).IsEqualTo(LoginPlan.WithoutPassword);
        await Assert.That(character.ResolveConnectString()).IsEqualTo("connect Corvid");
    }

    /// <summary>
    /// The one way left to store a credential that never goes anywhere: a line with no
    /// <c>%PASSWORD%</c> in it. It is its own answer rather than being lumped in with "sends", because
    /// the whole lesson of the bug this replaced is that a stored secret doing nothing must be visible.
    /// The login still goes out — the line is valid, it just has no credential in it.
    /// </summary>
    [Test]
    public async Task ASavedPasswordWithNoTokenToPutItInIsItsOwnAnswer()
    {
        var character = Character(password: "hunter2", connect: "connect %CHARACTER%");

        await Assert.That(character.Login()).IsEqualTo(LoginPlan.PasswordUnused);
        await Assert.That(character.ResolveConnectString()).IsEqualTo("connect Corvid");
    }

    /// <summary>
    /// A misspelt token is the same case, and deliberately so: <c>%PASWORD%</c> is sent verbatim (the
    /// template's unknown-token rule), so the password is just as unused as if the token were absent.
    /// An escaped one likewise — <c>%%PASSWORD%%</c> is the literal text and substitutes nothing.
    /// </summary>
    [Test]
    public async Task AMisspeltOrEscapedTokenLeavesThePasswordUnused()
    {
        await Assert.That(Character("hunter2", "connect %CHARACTER% %PASWORD%").Login())
            .IsEqualTo(LoginPlan.PasswordUnused);
        await Assert.That(Character("hunter2", "connect %CHARACTER% %%PASSWORD%%").Login())
            .IsEqualTo(LoginPlan.PasswordUnused);
        await Assert.That(Character("hunter2", "connect %CHARACTER% %PASSWORD").Login())
            .IsEqualTo(LoginPlan.PasswordUnused);
    }

    /// <summary>
    /// A template that resolves to nothing is nothing, whatever it looked like before substitution.
    /// <c>%PASSWORD%</c> alone with no password set is an empty line, and a bare newline at a login
    /// prompt is not "no command" — it is a command some servers answer to.
    /// </summary>
    [Test]
    public async Task ALineThatResolvesToNothingIsNothing()
    {
        await Assert.That(Character(connect: "%PASSWORD%").Login()).IsEqualTo(LoginPlan.Nothing);
        await Assert.That(Character(connect: "   ").Login()).IsEqualTo(LoginPlan.Nothing);
    }

    /// <summary>
    /// An empty password is not a password. Blanking the field on F5 stores <c>""</c> before it stores
    /// null, and a character that "has" one only in the sense that the string exists would otherwise
    /// send <c>connect Corvid</c> and read as configured.
    /// </summary>
    [Test]
    public async Task AnEmptyPasswordIsNotAPassword()
    {
        await Assert.That(Character(password: string.Empty).Login()).IsEqualTo(LoginPlan.Nothing);
    }

    // ---- The token scanner ------------------------------------------------------------------------

    /// <summary>
    /// <see cref="ConnectStringTemplate.UsesPassword"/> repeats <c>Resolve</c>'s syntax rules in its own
    /// loop, so this holds the two together: over every template either of them is asked about, "the
    /// scanner found a password token" and "substituting a password changed the output" must be the same
    /// answer. That is the drift a shared-constants argument would not catch.
    /// </summary>
    [Test]
    public async Task UsesPasswordAgreesWithResolveOnEveryTemplateShapeThereIs()
    {
        string?[] templates =
        {
            null, "", "   ",
            ConnectStringTemplate.Default,
            "connect %CHARACTER% %PASSWORD%", "co %CHARACTER% %PASSWORD%", "%PASSWORD%/%CHARACTER%",
            "%password%", "%Password%", "connect \"%CHARACTER%\" %PASSWORD%",
            "connect %CHARACTER%", "%CHARACTER% %CHARACTER%", "connect Corvid hunter2",
            "%%PASSWORD%%", "%%%PASSWORD%", "%PASWORD%", "%PASSWORD", "%", "%%", "%%%%",
            "say 100%% sure", "50% off", "connect %CHARACTER %PASSWORD%",
        };

        foreach (var template in templates)
        {
            // Two distinct non-empty values, so the empty-value space rule cannot muddy the comparison:
            // the outputs differ if and only if a password token was actually substituted.
            var substituted = ConnectStringTemplate.Resolve(template, "Corvid", "aaa")
                != ConnectStringTemplate.Resolve(template, "Corvid", "bbb");

            await Assert.That(ConnectStringTemplate.UsesPassword(template))
                .IsEqualTo(substituted)
                .Because($"template: {template ?? "<null>"}");
        }
    }

    // ---- The migration ----------------------------------------------------------------------------

    private static JsonObject Migrated(string json)
    {
        var root = (JsonObject)JsonNode.Parse(json)!;
        ConfigurationMigrator.Migrate(root);
        return root;
    }

    private static JsonObject CharacterIn(JsonObject root, int index = 0) =>
        (JsonObject)((JsonArray)((JsonObject)((JsonArray)root["worlds"]!)[0]!)["characters"]!)[index]!;

    /// <summary>
    /// <b>The reporter's two characters.</b> <c>autoLogin: false</c> beside a stored password is not a
    /// decision anybody made — a <c>bool</c> serializes on every character whether it was ever touched,
    /// and the control that would have changed it was never drawn. It is discarded, and the character
    /// logs in. Preserving it faithfully would have preserved the bug.
    /// </summary>
    [Test]
    public async Task AutoLoginFalseBesideASavedPasswordBecomesACharacterThatLogsIn()
    {
        var root = Migrated("""
        {
          "version": 3,
          "worlds": [ { "name": "Convergence MUSH", "host": "game.convergencemush.org", "characters": [
            { "name": "Mannaz", "autoLogin": false, "connectAtStartup": true,
              "passwordRef": "6f1d3f8e-0000-4000-8000-000000000001" } ] } ]
        }
        """);

        var character = CharacterIn(root);
        await Assert.That(character.ContainsKey("autoLogin")).IsFalse();

        // Nothing was written into the connect line: the password alone already says "log in", and the
        // default template is what puts it there.
        await Assert.That(character.ContainsKey("connectString")).IsFalse();
        await Assert.That(root["version"]!.GetValue<int>()).IsEqualTo(AppConfiguration.CurrentVersion);

        var loaded = new CharacterDefinition { Name = "Mannaz", Password = "hunter2" };
        await Assert.That(loaded.Login()).IsEqualTo(LoginPlan.WithPassword);
    }

    /// <summary>
    /// The one case that would otherwise lose behaviour: the flag on, no password, no connect line. It
    /// was sending <c>connect &lt;Name&gt;</c> through the default template's empty-value rule, and it is
    /// a real configuration on a passwordless world. The migration writes the line down rather than
    /// inferring it, so F5 shows the same thing the client will send.
    /// </summary>
    [Test]
    public async Task AutoLoginTrueWithNothingToSendKeepsSendingByWritingItsLineDown()
    {
        var root = Migrated("""
        {
          "version": 3,
          "worlds": [ { "name": "Aetherfall", "host": "aetherfall.mux", "characters": [
            { "name": "Corvid", "autoLogin": true, "onConnect": "@@ +who; look" } ] } ]
        }
        """);

        var character = CharacterIn(root);
        await Assert.That(character.ContainsKey("autoLogin")).IsFalse();
        await Assert.That(character["connectString"]!.GetValue<string>()).IsEqualTo("connect %CHARACTER%");

        // The identical string it was sending before, and its on-connect commands untouched.
        var loaded = new CharacterDefinition { Name = "Corvid", ConnectString = "connect %CHARACTER%" };
        await Assert.That(loaded.ResolveConnectString()).IsEqualTo("connect Corvid");
        await Assert.That(loaded.Login()).IsEqualTo(LoginPlan.WithoutPassword);
        await Assert.That(character["onConnect"]!.GetValue<string>()).IsEqualTo("@@ +who; look");
    }

    /// <summary>
    /// <c>autoLogin: true</c> with a connect line already written keeps that line — the migration must
    /// not overwrite something the user typed with the generic one it invents for the empty case.
    /// </summary>
    [Test]
    public async Task AutoLoginTrueWithItsOwnConnectLineKeepsIt()
    {
        var root = Migrated("""
        {
          "version": 3,
          "worlds": [ { "name": "Aetherfall", "host": "aetherfall.mux", "characters": [
            { "name": "Corvid", "autoLogin": true, "connectString": "co %CHARACTER% %PASSWORD%" } ] } ]
        }
        """);

        await Assert.That(CharacterIn(root)["connectString"]!.GetValue<string>())
            .IsEqualTo("co %CHARACTER% %PASSWORD%");
    }

    /// <summary>
    /// And the state that must stay silent across the upgrade: no flag, no password, no line. It sent
    /// nothing before and sends nothing now — the migration does not hand a login line to somebody who
    /// never had one, which would start typing at a prompt on every connect they make.
    /// </summary>
    [Test]
    public async Task ACharacterWithNothingConfiguredIsLeftWithNothing()
    {
        var root = Migrated("""
        {
          "version": 3,
          "worlds": [ { "name": "Grapevine", "host": "grapevine.haus", "characters": [
            { "name": "Thistle", "autoLogin": false } ] } ]
        }
        """);

        var character = CharacterIn(root);
        await Assert.That(character.ContainsKey("autoLogin")).IsFalse();
        await Assert.That(character.ContainsKey("connectString")).IsFalse();
        await Assert.That(new CharacterDefinition { Name = "Thistle" }.Login()).IsEqualTo(LoginPlan.Nothing);
    }

    /// <summary>
    /// A document already at the current version is not re-migrated — a v4 character that deliberately
    /// has no connect line must not acquire one because some later reader saw no <c>autoLogin</c> key
    /// and treated its absence as <c>true</c>. (It reads as <c>false</c>; this pins that it is never
    /// reached at all.)
    /// </summary>
    [Test]
    public async Task ACurrentDocumentIsLeftAlone()
    {
        var json = JsonSerializer.Serialize(JsonNode.Parse($$"""
        {
          "version": {{AppConfiguration.CurrentVersion}},
          "worlds": [ { "name": "Grapevine", "host": "grapevine.haus", "characters": [
            { "name": "Thistle" } ] } ]
        }
        """));

        var root = Migrated(json);

        await Assert.That(CharacterIn(root).ContainsKey("connectString")).IsFalse();
    }
}
