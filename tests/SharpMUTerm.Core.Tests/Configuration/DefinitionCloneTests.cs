using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Tests.Configuration;

/// <summary>
/// <see cref="WorldDefinition.Clone"/> and <see cref="CharacterDefinition.Clone"/> — the deep copy the
/// F5 screen's <c>duplicate</c> button is built on. A copy that shared a list or a settings object
/// with its original would look correct on screen and only betray itself later, when an edit to one
/// silently landed on both, so every mutable part is asserted to be genuinely separate rather than
/// merely equal.
/// </summary>
public class DefinitionCloneTests
{
    private static CharacterDefinition Character() => new()
    {
        Name = "Kaz",
        Password = "hunter2",
        ConnectString = "connect Kaz hunter2",
        AutoLogin = true,
        OnConnect = "look",
        OnDisconnect = "quit",
        TriggerSets = new List<string> { "Comms", "Combat" },
        Logging = new LoggingSettings { Format = LogFormat.Html, Directory = "/logs/kaz" },
    };

    [Test]
    public async Task CharacterClone_CopiesEveryValue()
    {
        var copy = Character().Clone();

        await Assert.That(copy.Name).IsEqualTo("Kaz");
        await Assert.That(copy.Password).IsEqualTo("hunter2");
        await Assert.That(copy.ConnectString).IsEqualTo("connect Kaz hunter2");
        await Assert.That(copy.AutoLogin).IsTrue();
        await Assert.That(copy.OnConnect).IsEqualTo("look");
        await Assert.That(copy.OnDisconnect).IsEqualTo("quit");
        await Assert.That(copy.TriggerSets).IsEquivalentTo(new[] { "Comms", "Combat" });
        await Assert.That(copy.Logging.Format).IsEqualTo(LogFormat.Html);
        await Assert.That(copy.Logging.Directory).IsEqualTo("/logs/kaz");
    }

    [Test]
    public async Task CharacterClone_SharesNoMutableStateWithItsOriginal()
    {
        var original = Character();
        var copy = original.Clone();

        await Assert.That(ReferenceEquals(original.TriggerSets, copy.TriggerSets)).IsFalse();
        await Assert.That(ReferenceEquals(original.Logging, copy.Logging)).IsFalse();

        copy.Name = "Kaz copy";
        copy.TriggerSets.Add("Trade");
        copy.TriggerSets.Remove("Comms");
        copy.Logging.Format = LogFormat.None;
        copy.Logging.Directory = "/logs/copy";
        copy.AutoLogin = false;

        await Assert.That(original.Name).IsEqualTo("Kaz");
        await Assert.That(original.TriggerSets).IsEquivalentTo(new[] { "Comms", "Combat" });
        await Assert.That(original.Logging.Format).IsEqualTo(LogFormat.Html);
        await Assert.That(original.Logging.Directory).IsEqualTo("/logs/kaz");
        await Assert.That(original.AutoLogin).IsTrue();

        // And in the other direction — an aliasing bug is only half-visible from one side.
        original.TriggerSets.Add("Guild");
        await Assert.That(copy.TriggerSets).DoesNotContain("Guild");
    }

    [Test]
    public async Task WorldClone_CopiesEveryValue_AndDeepCopiesItsCharacters()
    {
        var world = new WorldDefinition
        {
            Name = "Aardwolf",
            Host = "aardmud.org",
            Port = 4000,
            UseTls = true,
            AllowInvalidCertificates = true,
            LocalEcho = false,
            Encoding = "ISO-8859-1",
            KeepaliveSeconds = 60,
            ContentFormat = ContentFormat.Mxp,
            Emoji = new EmojiSettings { Enabled = true, Emoticons = false, Shortcodes = true },
            Accent = TerminalColor.FromRgb(0x00, 0xf5, 0xb7),
            Characters = new List<CharacterDefinition> { Character() },
        };

        var copy = world.Clone();

        await Assert.That(copy.Name).IsEqualTo("Aardwolf");
        await Assert.That(copy.Host).IsEqualTo("aardmud.org");
        await Assert.That(copy.Port).IsEqualTo(4000);
        await Assert.That(copy.UseTls).IsTrue();
        await Assert.That(copy.AllowInvalidCertificates).IsTrue();
        await Assert.That(copy.LocalEcho).IsFalse();
        await Assert.That(copy.Encoding).IsEqualTo("ISO-8859-1");
        await Assert.That(copy.KeepaliveSeconds).IsEqualTo(60);
        await Assert.That(copy.ContentFormat).IsEqualTo(ContentFormat.Mxp);
        await Assert.That(copy.Accent).IsEqualTo(TerminalColor.FromRgb(0x00, 0xf5, 0xb7));
        await Assert.That(copy.Characters.Count).IsEqualTo(1);

        await Assert.That(ReferenceEquals(world.Characters, copy.Characters)).IsFalse();
        await Assert.That(ReferenceEquals(world.Characters[0], copy.Characters[0])).IsFalse();
        await Assert.That(ReferenceEquals(world.Emoji, copy.Emoji)).IsFalse();

        copy.Characters[0].Name = "Mira";
        copy.Characters[0].TriggerSets.Clear();
        copy.Emoji.Enabled = false;
        copy.Characters.Add(new CharacterDefinition());

        await Assert.That(world.Characters.Count).IsEqualTo(1);
        await Assert.That(world.Characters[0].Name).IsEqualTo("Kaz");
        await Assert.That(world.Characters[0].TriggerSets.Count).IsEqualTo(2);
        await Assert.That(world.Emoji.Enabled).IsTrue();
    }
}
