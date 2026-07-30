using System.Text;
using SharpMUTerm.Core.Telnet;

namespace SharpMUTerm.Core.Tests.Telnet;

/// <summary>
/// A world's <c>encoding</c> (F5) is the head of the CHARSET preference order it negotiates with.
/// Before this it was drawn in the status bar and nowhere else — the session always offered UTF-8
/// first whatever the field said.
/// </summary>
public class CharsetOrderTests
{
    [Test]
    public async Task PreferEncoding_PutsTheWorldsEncodingFirst()
    {
        var order = TelnetSessionOptions.PreferEncoding("ISO-8859-1");

        await Assert.That(order[0].CodePage).IsEqualTo(Encoding.Latin1.CodePage);
    }

    /// <summary>
    /// The rest of the default order stays behind it as fallbacks, each listed once: preference order
    /// is a negotiation, and a server that won't speak the head of the list has to land somewhere.
    /// </summary>
    [Test]
    public async Task PreferEncoding_KeepsTheDefaultsBehindItWithoutDuplicating()
    {
        var order = TelnetSessionOptions.PreferEncoding("ISO-8859-1");

        await Assert.That(order.Length).IsEqualTo(2);
        await Assert.That(order[1].CodePage).IsEqualTo(Encoding.UTF8.CodePage);
        await Assert.That(order.Select(e => e.CodePage).Distinct().Count()).IsEqualTo(order.Length);
    }

    /// <summary>
    /// Every encoding we state must compare equal to the instance this machine's encoding provider
    /// yields for the same charset. <b>This assertion is the inverse of the one it replaces</b>, which
    /// required a BOM-less <c>new UTF8Encoding(false)</c> at the head so no preamble could be emitted,
    /// and that is not a weakening — the old instance was breaking negotiation and the hazard it
    /// guarded against does not exist here.
    /// <para>
    /// TelnetNegotiationCore ranks the charsets a server offers by <c>IndexOf</c> over the list we
    /// state, against encodings it gets from <see cref="Encoding.GetEncodings"/>. <see cref="UTF8Encoding"/>
    /// compares its BOM flag in <see cref="object.Equals(object)"/>, so the BOM-less instance matched
    /// nothing the library looked up, scored −1, and sorted <em>below</em> every charset that did
    /// match: a server offering <c>UTF-8;ISO-8859-1</c> was answered with Latin-1 by a client whose
    /// first preference was UTF-8. And a preamble was never at risk — only
    /// <see cref="Encoding.GetPreamble"/> produces one, which nothing in this stack calls;
    /// <see cref="Encoding.GetBytes(string)"/> does not.
    /// </para>
    /// </summary>
    [Test]
    public async Task PreferEncoding_UsesTheProvidersOwnInstances_SoNegotiationCanRankThem()
    {
        var order = TelnetSessionOptions.PreferEncoding("UTF-8");
        var provider = Encoding.GetEncodings().Select(e => e.GetEncoding()).ToArray();

        await Assert.That(order[0].CodePage).IsEqualTo(Encoding.UTF8.CodePage);
        await Assert.That(order.Length).IsEqualTo(2);
        foreach (var encoding in order)
        {
            await Assert.That(provider.Contains(encoding)).IsTrue();
        }

        // And the head really does out-rank the tail under the library's own comparison, which is the
        // property the instances exist to satisfy.
        var ranked = provider
            .Where(e => order.Contains(e))
            .OrderByDescending(e => order.Reverse().ToList().IndexOf(e))
            .ToArray();
        await Assert.That(ranked[0].CodePage).IsEqualTo(Encoding.UTF8.CodePage);
    }

    /// <summary>Encoding text still emits no byte-order mark, which is what the old pin actually cared about.</summary>
    [Test]
    public async Task PreferEncoding_Utf8_PutsNoByteOrderMarkOnTheWire()
    {
        var order = TelnetSessionOptions.PreferEncoding("UTF-8");

        await Assert.That(order[0].GetBytes("a").Length).IsEqualTo(1);
        await Assert.That(order[0].GetBytes("café")).IsEquivalentTo(Encoding.UTF8.GetBytes("café"));
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("not-an-encoding")]
    public async Task PreferEncoding_UnknownOrMissing_FallsBackToTheDefaultOrder(string? name)
    {
        var order = TelnetSessionOptions.PreferEncoding(name);

        await Assert.That(order[0].CodePage).IsEqualTo(Encoding.UTF8.CodePage);
        await Assert.That(order[1].CodePage).IsEqualTo(Encoding.Latin1.CodePage);
    }

    /// <summary>The options a session is built with default to the same order, so nothing regressed.</summary>
    [Test]
    public async Task DefaultOptions_StillLeadWithUtf8()
    {
        var options = new TelnetSessionOptions();

        await Assert.That(options.CharsetOrder[0].CodePage).IsEqualTo(Encoding.UTF8.CodePage);
    }
}
