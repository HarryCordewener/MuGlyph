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
    /// UTF-8 asked for by name resolves to the BOM-less instance the default order already holds, not
    /// to <see cref="Encoding.UTF8"/>, which emits a preamble.
    /// </summary>
    [Test]
    public async Task PreferEncoding_Utf8_StaysBomless()
    {
        var order = TelnetSessionOptions.PreferEncoding("UTF-8");

        await Assert.That(order[0].CodePage).IsEqualTo(Encoding.UTF8.CodePage);
        await Assert.That(order[0].GetPreamble().Length).IsEqualTo(0);
        await Assert.That(order.Length).IsEqualTo(2);
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
