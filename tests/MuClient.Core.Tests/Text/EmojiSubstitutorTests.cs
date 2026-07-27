using MuClient.Core.Text;

namespace MuClient.Core.Tests.Text;

public class EmojiSubstitutorTests
{
    [Test]
    public async Task Emoticon_IsReplaced_WhenTokenBounded()
    {
        var sub = new EmojiSubstitutor();
        await Assert.That(sub.Apply("hello :) there")).IsEqualTo("hello 🙂 there");
    }

    [Test]
    public async Task Emoticon_AtStartAndEnd()
    {
        var sub = new EmojiSubstitutor();
        await Assert.That(sub.Apply(":) hi")).IsEqualTo("🙂 hi");
        await Assert.That(sub.Apply("bye :(")).IsEqualTo("bye 🙁");
    }

    [Test]
    public async Task Emoticon_InsideWord_IsNotReplaced()
    {
        var sub = new EmojiSubstitutor();
        // Should not fire inside a URL or word.
        await Assert.That(sub.Apply("http://x:)y")).IsEqualTo("http://x:)y");
    }

    [Test]
    public async Task Shortcode_IsReplaced()
    {
        var sub = new EmojiSubstitutor();
        await Assert.That(sub.Apply("that was :fire: hot")).IsEqualTo("that was 🔥 hot");
    }

    [Test]
    public async Task Shortcode_IsCaseInsensitive()
    {
        var sub = new EmojiSubstitutor();
        await Assert.That(sub.Apply(":HEART:")).IsEqualTo("❤️");
    }

    [Test]
    public async Task UnknownShortcode_IsLeftAlone()
    {
        var sub = new EmojiSubstitutor();
        await Assert.That(sub.Apply("ratio 3:2 done")).IsEqualTo("ratio 3:2 done");
        await Assert.That(sub.Apply(":notareal:")).IsEqualTo(":notareal:");
    }

    [Test]
    public async Task ExtraShortcodes_AreMerged()
    {
        var sub = new EmojiSubstitutor(extraShortcodes: new Dictionary<string, string> { ["mudlet"] = "🐲" });
        await Assert.That(sub.Apply(":mudlet:")).IsEqualTo("🐲");
        await Assert.That(sub.Apply(":fire:")).IsEqualTo("🔥"); // defaults still present
    }

    [Test]
    public async Task Disabled_LeavesTextUnchanged()
    {
        var sub = new EmojiSubstitutor(emoticons: false, shortcodes: false);
        await Assert.That(sub.Apply(":) :fire:")).IsEqualTo(":) :fire:");
    }

    [Test]
    public async Task Heart_Emoticon()
    {
        var sub = new EmojiSubstitutor();
        await Assert.That(sub.Apply("<3 you")).IsEqualTo("❤️ you");
    }
}
