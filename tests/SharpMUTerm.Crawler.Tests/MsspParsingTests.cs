using System.Text;
using SharpMUTerm.Core.Telnet.Mssp;
using SharpMUTerm.Crawler.Tests.Support;

namespace SharpMUTerm.Crawler.Tests;

/// <summary>
/// The wire parser, against payloads built byte by byte from the specification's own format.
/// </summary>
public class MsspParsingTests
{
    private static MsspData ParseOne(params (string Variable, string[] Values)[] entries)
    {
        var parser = new MsspSubnegotiationParser();
        var reports = parser.Consume(MsspWire.Subnegotiation(entries));
        return reports.Single();
    }

    [Test]
    public async Task AFullPayloadKeepsEveryVariableItWasSent()
    {
        var data = ParseOne(MsspWire.RepresentativeReport("mud.example.net 4000"));

        await Assert.That(data.Name).IsEqualTo("Corvid Nest");
        await Assert.That(data.Players).IsEqualTo(17);
        await Assert.That(data.Uptime).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(1735689600));
        await Assert.That(data.Hostname).IsEqualTo("corvid.example.org");
        await Assert.That(data.Contact).IsEqualTo("admin@corvid.example.org");
        await Assert.That(data.Website).IsEqualTo("https://corvid.example.org/");
    }

    [Test]
    public async Task ArrayNotationKeepsEveryValueInOrderWithTheDefaultLast()
    {
        // "It's also possible to attach several values to a single variable by using MSSP_VAL more than
        // once, with the default value reported last." This is the case TelnetNegotiationCore 2.6.0
        // concatenates into the integer 80234201.
        var data = ParseOne(("PORT", ["80", "23", "4201"]));

        await Assert.That(data["PORT"]).IsEquivalentTo(new[] { "80", "23", "4201" });
        await Assert.That(data.Ports).IsEquivalentTo(new[] { 80, 23, 4201 });
        await Assert.That(data.Port).IsEqualTo(4201);
    }

    [Test]
    public async Task ARepeatedVariableAccumulatesRatherThanReplacing()
    {
        // "The same variable can be send more than once with different values, in which case the last
        // reported value should be used as the default value." The two spellings of an array must end up
        // in one place, or a consumer would have to know which one a server chose.
        var data = ParseOne(("CODEBASE", ["Merc 2.1"]), ("CODEBASE", ["ROM 2.4"]));

        await Assert.That(data["CODEBASE"]).IsEquivalentTo(new[] { "Merc 2.1", "ROM 2.4" });
        await Assert.That(data.Codebase).IsEqualTo("ROM 2.4");
    }

    [Test]
    public async Task UnofficialAndWhollyUnknownVariablesAreKept()
    {
        var data = ParseOne(MsspWire.RepresentativeReport());

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
        // Every one of these is dropped by the telnet library: the booleans fail to bind from their
        // string form, and CHARSET is official but absent from its model.
        var data = ParseOne(MsspWire.RepresentativeReport());

        await Assert.That(data.Flag("ANSI")).IsTrue();
        await Assert.That(data.Flag("UTF-8")).IsTrue();
        await Assert.That(data.Flag("PAY TO PLAY")).IsFalse();
        await Assert.That(data["CHARSET"]).IsEquivalentTo(new[] { "ASCII", "UTF-8" });
    }

    [Test]
    public async Task TheVocabularyMatchesTheSpecificationsOwnTables()
    {
        // A crawler's whole job is to report what the protocol defines, so the list it defines it
        // against is worth pinning: the three required variables, the ones whose names contain spaces
        // (the case the underscore substitution exists for), and the boundary between official and not.
        await Assert.That(MsspVariables.Official.Count).IsEqualTo(45);

        foreach (var required in new[] { "NAME", "PLAYERS", "UPTIME" })
        {
            await Assert.That(MsspVariables.IsOfficial(required)).IsTrue();
        }

        foreach (var spaced in new[] { "CRAWL DELAY", "MINIMUM AGE", "XTERM 256 COLORS", "PAY TO PLAY", "HIRING CODERS" })
        {
            await Assert.That(MsspVariables.IsOfficial(spaced)).IsTrue();
            await Assert.That(MsspVariables.IsOfficial(spaced.Replace(' ', '_'))).IsTrue();
        }

        // Unofficial but widely deployed: recognised, and correctly not claimed as official.
        await Assert.That(MsspVariables.IsKnownUnofficial("PUEBLO")).IsTrue();
        await Assert.That(MsspVariables.IsOfficial("PUEBLO")).IsFalse();

        // Wholly unknown: neither, and still kept by the parser.
        await Assert.That(MsspVariables.IsOfficial("CORVID SPECIFIC")).IsFalse();
        await Assert.That(MsspVariables.IsKnownUnofficial("CORVID SPECIFIC")).IsFalse();

        // UTF-8 keeps its hyphen; nothing in the folding touches it.
        await Assert.That(MsspVariables.Canonicalise("utf-8")).IsEqualTo("UTF-8");
    }

    [Test]
    public async Task TheUnderscoreSpellingIsTheSameVariableAsTheSpacedOne()
    {
        // The specification's own recommendation: "clients and crawlers can substitute spaces with
        // underscores". A server may use either and mean one variable.
        var data = ParseOne(("MINIMUM_AGE", ["13"]), ("MINIMUM AGE", ["18"]));

        await Assert.That(data.Count).IsEqualTo(1);
        await Assert.That(data["MINIMUM AGE"]).IsEquivalentTo(new[] { "13", "18" });
        await Assert.That(data.Default("MINIMUM_AGE")).IsEqualTo("18");
    }

    [Test]
    public async Task VariableNamesAreMatchedWithoutRegardToCaseOrStrayWhitespace()
    {
        var data = ParseOne(("  crawl   delay ", ["11"]));

        await Assert.That(data.ContainsKey("CRAWL DELAY")).IsTrue();
        await Assert.That(data.CrawlDelay).IsEqualTo(TimeSpan.FromHours(11));
    }

    [Test]
    public async Task ACrawlDelayOfMinusOneMeansNoPreferenceRatherThanANegativeInterval()
    {
        // "Send -1 to use the crawler's default."
        await Assert.That(ParseOne(("CRAWL DELAY", ["-1"])).CrawlDelay).IsNull();
        await Assert.That(ParseOne(("CRAWL DELAY", ["23"])).CrawlDelay).IsEqualTo(TimeSpan.FromHours(23));
    }

    [Test]
    public async Task AWorldCountOfMinusOneMeansUnavailableRatherThanMinusOne()
    {
        // "If your Mud can't calculate one of the numeric values for the World variables you can use
        // -1 to indicate that the data is not available."
        await Assert.That(ParseOne(("ROOMS", ["-1"])).Integer("ROOMS")).IsNull();
        await Assert.That(ParseOne(("ROOMS", ["0"])).Integer("ROOMS")).IsEqualTo(0);
    }

    [Test]
    public async Task AnEmptyValueIsAValueAndAVariableWithNoValueIsNot()
    {
        // "The value can be an empty string."
        var data = ParseOne(("CONTACT", [""]), ("ICON", []));

        await Assert.That(data.ContainsKey("CONTACT")).IsTrue();
        await Assert.That(data.Contact).IsEqualTo(string.Empty);

        await Assert.That(data.ContainsKey("ICON")).IsTrue();
        await Assert.That(data["ICON"]).IsEmpty();
        await Assert.That(data.Default("ICON")).IsNull();
    }

    [Test]
    public async Task APayloadSplitAcrossReadsParsesTheSameAsAWholeOne()
    {
        var whole = MsspWire.Subnegotiation(MsspWire.RepresentativeReport("a.example.org 4000"));

        var parser = new MsspSubnegotiationParser();
        MsspData? report = null;

        // One byte at a time — the worst case a TCP stream can produce, and the one an incremental
        // parser has to survive.
        foreach (var b in whole)
        {
            foreach (var completed in parser.Consume([b]))
            {
                report = completed;
            }
        }

        await Assert.That(report).IsNotNull();
        await Assert.That(report!.Name).IsEqualTo("Corvid Nest");
        await Assert.That(report.Referrals.Single().ToReferralString()).IsEqualTo("a.example.org 4000");
    }

    [Test]
    public async Task AnotherOptionsSubnegotiationCannotBeMistakenForMssp()
    {
        // A GMCP payload is arbitrary text and can contain the bytes 0xFF 0xFA 0x46 by coincidence. A
        // scanner that looked for MSSP anywhere in the stream would read that as the start of a report.
        var stream = new List<byte>();
        stream.AddRange([MsspWire.Iac, MsspWire.Sb, MsspWire.Gmcp]);
        stream.AddRange(Encoding.UTF8.GetBytes("Core.Hello {\"client\":\"x\"}"));
        stream.AddRange([MsspWire.Iac, MsspWire.Iac]); // an escaped 0xFF inside the payload
        stream.AddRange([MsspWire.Sb, MsspWire.Mssp, MsspWire.Var]);
        stream.AddRange(Encoding.UTF8.GetBytes("NAME"));
        stream.AddRange([MsspWire.Val]);
        stream.AddRange(Encoding.UTF8.GetBytes("Not A Real Report"));
        stream.AddRange([MsspWire.Iac, MsspWire.Se]);

        var parser = new MsspSubnegotiationParser();
        await Assert.That(parser.Consume([.. stream])).IsEmpty();

        // …and the real one that follows is still read.
        var real = parser.Consume(MsspWire.Subnegotiation(("NAME", ["Real"])));
        await Assert.That(real.Single().Name).IsEqualTo("Real");
    }

    [Test]
    public async Task AnEscapedIacInsideAValueIsALiteralByte()
    {
        var bytes = new List<byte> { MsspWire.Iac, MsspWire.Sb, MsspWire.Mssp, MsspWire.Var };
        bytes.AddRange(Encoding.UTF8.GetBytes("NAME"));
        bytes.Add(MsspWire.Val);
        bytes.AddRange(Encoding.Latin1.GetBytes("a"));
        bytes.AddRange([MsspWire.Iac, MsspWire.Iac]);
        bytes.AddRange(Encoding.Latin1.GetBytes("b"));
        bytes.AddRange([MsspWire.Iac, MsspWire.Se]);

        var parser = new MsspSubnegotiationParser(Encoding.Latin1);
        await Assert.That(parser.Consume([.. bytes]).Single().Name).IsEqualTo("aÿb");
    }

    [Test]
    public async Task NegotiationForOtherOptionsIsSteppedOverWithoutConfusingTheScanner()
    {
        // IAC DO/WILL/WONT/DONT <option> is three bytes; a scanner that did not know that would read the
        // option byte as the start of something.
        var stream = new List<byte>();
        stream.AddRange([MsspWire.Iac, MsspWire.Will, MsspWire.Mssp]);
        stream.AddRange([MsspWire.Iac, MsspWire.Do, MsspWire.Sb]); // option 250, deliberately awkward
        stream.AddRange([MsspWire.Iac, MsspWire.Wont, MsspWire.Mssp]);
        stream.AddRange(MsspWire.Subnegotiation(("NAME", ["Survived"])));

        var parser = new MsspSubnegotiationParser();
        await Assert.That(parser.Consume([.. stream]).Single().Name).IsEqualTo("Survived");
    }

    [Test]
    public async Task AnEnormousPayloadIsTruncatedRatherThanBuffered()
    {
        // A remote server must not be able to decide how much memory a crawler spends on it.
        var parser = new MsspSubnegotiationParser(maxPayloadBytes: 128);
        var report = parser.Consume(MsspWire.Subnegotiation(("NAME", [new string('x', 4096)]))).Single();

        await Assert.That(parser.Truncated).IsTrue();
        await Assert.That(report.Name!.Length).IsEqualTo(128);
    }

    [Test]
    public async Task AnUnterminatedPayloadYieldsNothingRatherThanAPartialReport()
    {
        var whole = MsspWire.Subnegotiation(("NAME", ["Half"]));
        var parser = new MsspSubnegotiationParser();

        await Assert.That(parser.Consume(whole.AsSpan(0, whole.Length - 2).ToArray())).IsEmpty();
    }
}
