using System.Text;
using SharpMUTerm.Core.Telnet.Mssp;
using SharpMUTerm.Crawler.Model;
using SharpMUTerm.Crawler.Probing;
using SharpMUTerm.Crawler.Tests.Support;
using TelnetNegotiationCore.Models;

namespace SharpMUTerm.Crawler.Tests;

/// <summary>
/// What a server's MSSP report turns into, against payloads built byte by byte from the
/// specification's own format and fed through a real telnet session.
/// <para>
/// <b>There is no MSSP parser in this repository any more.</b> These cases used to pin one, written
/// because TelnetNegotiationCore's reader destroyed arrays, booleans and unknown variables before any
/// consumer saw them. That is fixed upstream (2.6.5), so the same cases now run against the library's
/// own <c>MSSPConfig.Variables</c> by way of <see cref="MsspData"/>, and prove the replacement keeps
/// what the workaround kept. The two that <em>diverge</em> are named as such and say why.
/// </para>
/// </summary>
public class MsspParsingTests
{
    private static MsspHost Host => MsspHost.Create("server.example.org", 4201)!;

    private static CrawlOptions Options => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        MsspTimeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>
    /// One server, one report: a scripted server offers MSSP, answers the crawler's <c>DO</c> with
    /// <paramref name="entries"/>, and the report the probe came back with is returned.
    /// </summary>
    private static Task<MsspData> Read(params (string Variable, string[] Values)[] entries) =>
        ReadRaw(MsspWire.Subnegotiation(entries));

    private static async Task<MsspData> ReadRaw(byte[] payload, bool fragmented = false)
    {
        var transport = new ScriptedTransport { Greeting = MsspWire.Offer(), Fragmented = fragmented }
            .RespondingToDo(MsspWire.Mssp, payload);

        var result = await new TelnetMsspProbe(Options, _ => transport).ProbeAsync(Host, CancellationToken.None);

        return result.Outcome == CrawlOutcome.MsspReceived && result.Data is { } data
            ? data
            : throw new InvalidOperationException($"No MSSP report: {result.Outcome} ({result.Error}).");
    }

    [Test]
    public async Task AFullPayloadKeepsEveryVariableItWasSent()
    {
        var data = await Read(MsspWire.RepresentativeReport("mud.example.net 4000"));

        await Assert.That(data.Name).IsEqualTo("Corvid Nest");
        await Assert.That(data.Players).IsEqualTo(17);
        await Assert.That(data.Uptime).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(1735689600));
        await Assert.That(data.Hostname).IsEqualTo("corvid.example.org");
        await Assert.That(data.Contact).IsEqualTo("admin@corvid.example.org");
        await Assert.That(data.Website).IsEqualTo("https://corvid.example.org/");
        await Assert.That(data.Count).IsEqualTo(22);
    }

    [Test]
    public async Task ArrayNotationKeepsEveryValueInOrderWithTheDefaultLast()
    {
        // "It's also possible to attach several values to a single variable by using MSSP_VAL more than
        // once, with the default value reported last." This is the case 2.6.0 concatenated into the
        // integer 80234201, and the reason a parser was written here at all.
        var data = await Read(("PORT", ["80", "23", "4201"]));

        await Assert.That(data["PORT"]).IsEquivalentTo(new[] { "80", "23", "4201" });
        await Assert.That(data.Ports).IsEquivalentTo(new[] { 80, 23, 4201 });
        await Assert.That(data.Port).IsEqualTo(4201);
    }

    [Test]
    public async Task TheReferralListArrivesAsAList()
    {
        // REFERRAL is array-only and is what a crawler follows; 2.6.0 delivered it as null.
        var data = await Read(("REFERRAL", ["a.example.org 4000", "b.example.net 23", "2001:db8::5 4201"]));

        await Assert.That(data["REFERRAL"]).Count().IsEqualTo(3);
        await Assert.That(data.Referrals.Select(r => r.ToReferralString()))
            .IsEquivalentTo(new[] { "a.example.org 4000", "b.example.net 23", "2001:db8::5 4201" });
    }

    [Test]
    public async Task ARepeatedVariableAccumulatesRatherThanReplacing()
    {
        // "The same variable can be send more than once with different values, in which case the last
        // reported value should be used as the default value." The two spellings of an array must end up
        // in one place, or a consumer would have to know which one a server chose.
        var data = await Read(("CODEBASE", ["Merc 2.1"]), ("CODEBASE", ["ROM 2.4"]));

        await Assert.That(data["CODEBASE"]).IsEquivalentTo(new[] { "Merc 2.1", "ROM 2.4" });
        await Assert.That(data.Codebase).IsEqualTo("ROM 2.4");
    }

    [Test]
    public async Task UnofficialAndWhollyUnknownVariablesAreKept()
    {
        var data = await Read(MsspWire.RepresentativeReport());

        // Unofficial but real.
        await Assert.That(data.Flag("PUEBLO")).IsTrue();
        await Assert.That(data.Flag("MSP")).IsFalse();

        // Invented by somebody's codebase; nothing in any model anywhere knows it.
        await Assert.That(data.Default("CORVID SPECIFIC")).IsEqualTo("nevermore");
        await Assert.That(data["VANITY"]).IsEquivalentTo(new[] { "least", "most" });

        await Assert.That(data.UnofficialNames)
            .Contains("PUEBLO").And.Contains("CORVID SPECIFIC").And.Contains("VANITY");
        await Assert.That(data.OfficialNames).Contains("NAME").And.Contains("CHARSET");
        await Assert.That(data.UnofficialNames).DoesNotContain("CHARSET");
    }

    [Test]
    public async Task BooleanAndCharsetVariablesSurvive()
    {
        // Every one of these was dropped by 2.6.0: the booleans failed to bind from their string form,
        // and CHARSET is official but had no property on the model.
        var data = await Read(MsspWire.RepresentativeReport());

        await Assert.That(data.Flag("ANSI")).IsTrue();
        await Assert.That(data.Flag("UTF-8")).IsTrue();
        await Assert.That(data.Flag("PAY TO PLAY")).IsFalse();
        await Assert.That(data["CHARSET"]).IsEquivalentTo(new[] { "ASCII", "UTF-8" });
    }

    [Test]
    public async Task TheVocabularyMatchesTheSpecificationsOwnTables()
    {
        // The vocabulary is the telnet library's now — one list, derived from the same model that reads
        // the wire — so this pins *its* list rather than a second copy. The three required variables,
        // the ones whose names contain spaces (the case the underscore substitution exists for), and
        // the boundary between official and not.
        await Assert.That(MSSPVariables.Official.Count).IsEqualTo(45);

        foreach (var required in new[] { "NAME", "PLAYERS", "UPTIME" })
        {
            await Assert.That(MSSPVariables.IsOfficial(required)).IsTrue();
        }

        foreach (var spaced in new[] { "CRAWL DELAY", "MINIMUM AGE", "XTERM 256 COLORS", "PAY TO PLAY", "HIRING CODERS" })
        {
            await Assert.That(MSSPVariables.IsOfficial(spaced)).IsTrue();
            await Assert.That(MSSPVariables.IsOfficial(spaced.Replace(' ', '_'))).IsTrue();
        }

        // The two the specification's tables omit and 2.6.5 added: CHARSET and DISCORD.
        await Assert.That(MSSPVariables.IsOfficial(MsspVariables.Charset)).IsTrue();
        await Assert.That(MSSPVariables.IsOfficial("DISCORD")).IsTrue();

        // Unofficial but widely deployed: modelled, and correctly not claimed as official.
        await Assert.That(MSSPVariables.IsKnown("PUEBLO")).IsTrue();
        await Assert.That(MSSPVariables.IsOfficial("PUEBLO")).IsFalse();

        // Wholly unknown: neither, and still kept in the report.
        await Assert.That(MSSPVariables.IsOfficial("CORVID SPECIFIC")).IsFalse();
        await Assert.That(MSSPVariables.IsKnown("CORVID SPECIFIC")).IsFalse();

        // UTF-8 keeps its hyphen; nothing in the folding touches it.
        await Assert.That(MSSPVariables.Canonicalize("utf-8")).IsEqualTo("UTF-8");
    }

    [Test]
    public async Task TheUnderscoreSpellingIsTheSameVariableAsTheSpacedOne()
    {
        // The specification's own recommendation: "clients and crawlers can substitute spaces with
        // underscores". A server may use either and mean one variable. 2.6.0 bound neither, because it
        // matched names with a culture-sensitive ToUpper() and no substitution.
        var data = await Read(("MINIMUM_AGE", ["13"]), ("MINIMUM AGE", ["18"]));

        await Assert.That(data.Count).IsEqualTo(1);
        await Assert.That(data["MINIMUM AGE"]).IsEquivalentTo(new[] { "13", "18" });
        await Assert.That(data.Default("MINIMUM_AGE")).IsEqualTo("18");
    }

    [Test]
    public async Task VariableNamesAreMatchedWithoutRegardToCaseOrStrayWhitespace()
    {
        var data = await Read(("  crawl   delay ", ["11"]));

        await Assert.That(data.ContainsKey("CRAWL DELAY")).IsTrue();
        await Assert.That(data.CrawlDelay).IsEqualTo(TimeSpan.FromHours(11));
    }

    [Test]
    public async Task ACrawlDelayOfMinusOneMeansNoPreferenceRatherThanANegativeInterval()
    {
        // "Send -1 to use the crawler's default." The library's own Integer() hands -1 back as-is,
        // deliberately; the reading that a scheduler can use is this projection's.
        await Assert.That((await Read(("CRAWL DELAY", ["-1"]))).CrawlDelay).IsNull();
        await Assert.That((await Read(("CRAWL DELAY", ["23"]))).CrawlDelay).IsEqualTo(TimeSpan.FromHours(23));
    }

    [Test]
    public async Task AWorldCountOfMinusOneMeansUnavailableRatherThanMinusOne()
    {
        // "If your Mud can't calculate one of the numeric values for the World variables you can use
        // -1 to indicate that the data is not available." Again the library returns -1 and this
        // projection returns null; the raw string is still one indexer away.
        var unavailable = await Read(("ROOMS", ["-1"]));
        await Assert.That(unavailable.Integer("ROOMS")).IsNull();
        await Assert.That(unavailable["ROOMS"]).IsEquivalentTo(new[] { "-1" });

        await Assert.That((await Read(("ROOMS", ["0"]))).Integer("ROOMS")).IsEqualTo(0);
    }

    [Test]
    public async Task AnEmptyValueIsAValueAndAVariableWithNoValueIsNot()
    {
        // "The value can be an empty string." A variable with no MSSP_VAL at all is malformed, but a
        // server that sends one used to wedge MSSP dead for the whole connection — EscapingMSSPVar
        // permitted only IAC, so the SE that followed the name went unhandled.
        var data = await Read(("CONTACT", [""]), ("ICON", []));

        await Assert.That(data.ContainsKey("CONTACT")).IsTrue();
        await Assert.That(data.Contact).IsEqualTo(string.Empty);

        await Assert.That(data.ContainsKey("ICON")).IsTrue();
        await Assert.That(data["ICON"]).IsEmpty();
        await Assert.That(data.Default("ICON")).IsNull();
    }

    [Test]
    public async Task AVariableWithNoValueDoesNotStopTheRestOfTheReportBeingRead()
    {
        // The other half of the wedge: everything after the valueless variable has to survive.
        var data = await Read(("ICON", []), ("NAME", ["After"]), ("PORT", ["23", "4201"]));

        await Assert.That(data["ICON"]).IsEmpty();
        await Assert.That(data.Name).IsEqualTo("After");
        await Assert.That(data.Ports).IsEquivalentTo(new[] { 23, 4201 });
    }

    [Test]
    public async Task APayloadSplitAcrossReadsParsesTheSameAsAWholeOne()
    {
        // One byte per read — the worst case a TCP stream can produce.
        var data = await ReadRaw(
            MsspWire.Subnegotiation(MsspWire.RepresentativeReport("a.example.org 4000")),
            fragmented: true);

        await Assert.That(data.Name).IsEqualTo("Corvid Nest");
        await Assert.That(data.Ports).IsEquivalentTo(new[] { 80, 23, 4201 });
        await Assert.That(data.Referrals.Single().ToReferralString()).IsEqualTo("a.example.org 4000");
    }

    [Test]
    public async Task AnotherOptionsSubnegotiationCannotBeMistakenForMssp()
    {
        // A GMCP payload is arbitrary text and can contain the bytes 0xFF 0xFA 0x46 by coincidence. A
        // reader that looked for MSSP anywhere in the stream would read that as the start of a report.
        var stream = new List<byte>();
        stream.AddRange([MsspWire.Iac, MsspWire.Sb, MsspWire.Gmcp]);
        stream.AddRange(Encoding.UTF8.GetBytes("Core.Hello {\"client\":\"x\"}"));
        stream.AddRange([MsspWire.Iac, MsspWire.Iac]); // an escaped 0xFF inside the payload
        stream.AddRange([MsspWire.Sb, MsspWire.Mssp, MsspWire.Var]);
        stream.AddRange(Encoding.UTF8.GetBytes("NAME"));
        stream.AddRange([MsspWire.Val]);
        stream.AddRange(Encoding.UTF8.GetBytes("Not A Real Report"));
        stream.AddRange([MsspWire.Iac, MsspWire.Se]);

        // …and the real one that follows is still read.
        stream.AddRange(MsspWire.Subnegotiation(("NAME", ["Real"])));

        await Assert.That((await ReadRaw([.. stream])).Name).IsEqualTo("Real");
    }

    [Test]
    public async Task NegotiationForOtherOptionsIsSteppedOverWithoutConfusingTheReader()
    {
        // IAC DO/WILL/WONT/DONT <option> is three bytes; a reader that did not know that would take the
        // option byte for the start of something.
        var stream = new List<byte>();
        stream.AddRange([MsspWire.Iac, MsspWire.Do, 31]);    // DO NAWS
        stream.AddRange([MsspWire.Iac, MsspWire.Wont, 86]);  // WONT MCCP2
        stream.AddRange(MsspWire.Subnegotiation(("NAME", ["Survived"])));

        await Assert.That((await ReadRaw([.. stream])).Name).IsEqualTo("Survived");
    }

    [Test]
    public async Task AnUnterminatedPayloadYieldsNothingRatherThanAPartialReport()
    {
        var whole = MsspWire.Subnegotiation(("NAME", ["Half"]));
        var transport = new ScriptedTransport { Greeting = MsspWire.Offer() }
            .RespondingToDo(MsspWire.Mssp, whole.AsSpan(0, whole.Length - 2).ToArray());

        var result = await new TelnetMsspProbe(
            Options with { MsspTimeout = TimeSpan.FromMilliseconds(400) }, _ => transport)
            .ProbeAsync(Host, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(CrawlOutcome.NoMssp);
        await Assert.That(result.Data).IsNull();
    }

    [Test]
    public async Task ANonAsciiValueIsMangledBecauseTheLibraryDecodesMsspAsAscii()
    {
        // DIVERGENCE from the parser this replaced, and an upstream defect rather than a decision:
        // MSSPProtocol.FlushField decodes every field with Encoding.ASCII rather than the session's
        // CurrentEncoding, so a MUD whose NAME, WEBSITE or CONTACT is not ASCII arrives as question
        // marks — one per *byte*, so "Café" becomes "Caf??" under UTF-8. The parser deleted here read
        // each field with whatever CHARSET had settled on.
        //
        // Reported upstream. When it is fixed this test fails, and the assertion below becomes
        // "Café Noir" rather than being deleted.
        var data = await Read(("NAME", ["Café Noir"]));

        await Assert.That(data.Name).IsEqualTo("Caf?? Noir");
    }

    [Test]
    public async Task AnEscapedIacInsideAValueLosesTheLiteralByte()
    {
        // DIVERGENCE, and the second upstream defect. IAC IAC inside a subnegotiation payload is an
        // escaped 0xFF; the MSSP state machine transitions EscapingMSSPVal -> EvaluatingMSSPVal on IAC
        // but registers no capture handler for that trigger, so the byte is dropped. The parser deleted
        // here kept it. MSSP forbids IAC inside a value, so this is only reachable on a malformed
        // payload — which is why it is a one-byte loss and not a lost report.
        var bytes = new List<byte> { MsspWire.Iac, MsspWire.Sb, MsspWire.Mssp, MsspWire.Var };
        bytes.AddRange(Encoding.ASCII.GetBytes("NAME"));
        bytes.Add(MsspWire.Val);
        bytes.AddRange(Encoding.ASCII.GetBytes("a"));
        bytes.AddRange([MsspWire.Iac, MsspWire.Iac]);
        bytes.AddRange(Encoding.ASCII.GetBytes("b"));
        bytes.AddRange([MsspWire.Iac, MsspWire.Se]);

        await Assert.That((await ReadRaw([.. bytes])).Name).IsEqualTo("ab");
    }
}
