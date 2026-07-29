using SharpMUTerm.Core.Input;

namespace SharpMUTerm.Core.Tests.Input;

/// <summary>
/// The credential ignore rule. It exists because the ⌃R surface makes history browsable and searchable, so
/// a hand-typed <c>connect &lt;name&gt; &lt;password&gt;</c> recorded in it is a password on display —
/// bash's <c>HISTIGNORE</c>, applied to the MU* login verbs.
/// </summary>
public class HistorySecretsTests
{
    /// <summary>The lines that must never be recorded, in the spellings a user actually types.</summary>
    [Test]
    [Arguments("connect Corvid hunter2")]
    [Arguments("CONNECT Corvid hunter2")]          // MU* dispatch is case-insensitive; so is this
    [Arguments("connect  Corvid   hunter2")]        // runs of spaces are one separator
    [Arguments("cd Corvid hunter2")]                // connect-dark
    [Arguments("ch Corvid hunter2")]                // connect-hidden
    [Arguments("create Corvid hunter2")]            // choosing a password is no less a secret
    [Arguments("connect \"Two Words\" hunter2")]
    [Arguments("@password hunter2=hunter3")]
    [Arguments("@newpassword Corvid=hunter3")]
    [Arguments("@pcreate Corvid=hunter2")]
    public async Task ACredentialBearingLineIsRecognised(string command)
    {
        await Assert.That(HistorySecrets.LooksLikeCredential(command)).IsTrue();
    }

    /// <summary>
    /// And the lines that must still be recalled. A guest-style <c>connect &lt;name&gt;</c> has nothing to
    /// hide, and the verbs are only the verbs — a pose that happens to mention connecting is not a login.
    /// </summary>
    [Test]
    [Arguments("connect")]
    [Arguments("connect guest")]
    [Arguments("cd")]
    [Arguments("create")]
    [Arguments("@password")]
    [Arguments("look")]
    [Arguments("say connect Corvid hunter2")]        // the verb is `say`
    [Arguments("pose connects the two wires.")]
    [Arguments("@pemit me=connect Corvid hunter2")]
    [Arguments("")]
    [Arguments("   ")]
    public async Task AnythingElseIsNot(string command)
    {
        await Assert.That(HistorySecrets.LooksLikeCredential(command)).IsFalse();
    }

    [Test]
    public async Task NullIsNotACredential()
    {
        await Assert.That(HistorySecrets.LooksLikeCredential(null)).IsFalse();
    }

    /// <summary>The verbs are readable, so a screen or a report can name them rather than restating them.</summary>
    [Test]
    public async Task TheVerbsAreNamed()
    {
        await Assert.That(HistorySecrets.Verbs).Contains("connect");
        await Assert.That(HistorySecrets.Verbs).Contains("@password");
    }
}
