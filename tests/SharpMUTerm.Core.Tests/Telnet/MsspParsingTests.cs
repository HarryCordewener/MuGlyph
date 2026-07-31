using System.Text;
using SharpMUTerm.Core.Telnet;
using SharpMUTerm.Core.Telnet.Mssp;
using TelnetNegotiationCore.Models;

namespace SharpMUTerm.Core.Tests.Telnet;

/// <summary>
/// What a server's MSSP report turns into, against payloads built byte by byte from the
/// specification's own format and fed through a real <see cref="TelnetSession"/>.
/// <para>
/// <b>There is no MSSP parser in this repository.</b> These cases used to pin one, written because
/// TelnetNegotiationCore's reader destroyed arrays, booleans and unknown variables before any
/// consumer saw them. That is fixed upstream (2.6.5), so the same cases now run against the library's
/// own <c>MSSPConfig.Variables</c> by way of <see cref="MsspData"/>, and prove the replacement keeps
/// what the workaround kept. The two that <em>diverge</em> from the old behaviour are named as such
/// and say why.
/// </para>
/// <para>
/// The session is built the way a world's is — <see cref="TelnetSessionOptions.RequestOptions"/>
/// carrying <see cref="TelnetSessionOptions.MsspOption"/>, so the client asks rather than waits — and
/// the scripted server answers only a client that asked. These are therefore the pins for the INFO
/// screen's supply as much as for the model.
/// </para>
/// </summary>
public class MsspParsingTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>
    /// One server, one report: a scripted server offers MSSP, answers the client's <c>DO</c> with
    /// <paramref name="entries"/>, and the report the session raised is returned.
    /// </summary>
    private static Task<MsspData> Read(params (string Variable, string[] Values)[] entries) =>
        ReadRaw(MsspWire.Subnegotiation(entries));

    private static async Task<MsspData> ReadRaw(byte[] payload, bool fragmented = false)
    {
        var transport = new ScriptedTransport { Greeting = MsspWire.Offer(), Fragmented = fragmented }
            .RespondingToDo(MsspWire.Mssp, payload);

        return await ReadFrom(transport)
            ?? throw new InvalidOperationException("The session raised no MSSP report.");
    }

    /// <summary>
    /// Connects a real session to <paramref name="transport"/> and returns the first MSSP report it
    /// raises, or null when none arrives inside <see cref="Patience"/>. Null is a <em>result</em> here
    /// rather than a failure: "this server publishes no MSSP" is one of the three states the INFO
    /// screen has to tell apart, and one case below has that as its whole subject.
    /// </summary>
    private static async Task<MsspData?> ReadFrom(ScriptedTransport transport, TimeSpan? patience = null)
    {
        var received = new TaskCompletionSource<MsspData>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new TelnetSession(
            transport,
            options: new TelnetSessionOptions { RequestOptions = [TelnetSessionOptions.MsspOption] });
        session.MsspReceived += (_, e) => received.TrySetResult(e.Data);

        await session.ConnectAsync();

        var completed = await Task.WhenAny(received.Task, Task.Delay(patience ?? Patience));
        return completed == received.Task ? await received.Task : null;
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
        // REFERRAL is array-only; 2.6.0 delivered it as null. Nothing reads it by name any more — the
        // INFO screen renders it among the rest — so what must survive is the array, whole and in order.
        var data = await Read(("REFERRAL", ["a.example.org 4000", "b.example.net 23", "2001:db8::5 4201"]));

        await Assert.That(data["REFERRAL"])
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
        await Assert.That(data.Default("crawl_delay")).IsEqualTo("11");
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
        await Assert.That(data["REFERRAL"]).IsEquivalentTo(new[] { "a.example.org 4000" });
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

        await Assert.That(await ReadFrom(transport, TimeSpan.FromMilliseconds(400))).IsNull();
    }

    [Test]
    public async Task ANonAsciiValueArrivesAsText()
    {
        // This used to pin the opposite: MSSPProtocol.FlushField decoded every field with
        // Encoding.ASCII rather than the session's CurrentEncoding, so a MUD whose NAME, WEBSITE or
        // CONTACT was not ASCII arrived as question marks - one per *byte*, so "Café" became "Caf??".
        // Fixed upstream in TelnetNegotiationCore 2.7.0, which is what this now holds.
        var data = await Read(("NAME", ["Café Noir"]));

        await Assert.That(data.Name).IsEqualTo("Café Noir");
    }

    [Test]
    public async Task AnEscapedIacInsideAValueIsNoLongerDropped()
    {
        // This used to pin the byte being lost: the MSSP state machine transitioned
        // EscapingMSSPVal -> EvaluatingMSSPVal on IAC but registered no capture handler for that
        // trigger, so "a IAC IAC b" arrived as "ab". Fixed upstream in TelnetNegotiationCore 2.7.0.
        //
        // It arrives as U+FFFD rather than as U+00FF, and that is correct rather than a second bug:
        // the byte now survives the state machine and is then decoded with the session's encoding,
        // and a lone 0xFF is not valid UTF-8. What matters here is the length - three characters,
        // not two - because a dropped byte would silently shorten every value after it.
        //
        // MSSP forbids IAC inside a value ("variables and values cannot contain the MSSP_VAL,
        // MSSP_VAR, IAC, or NUL byte"), so this is only reachable on a malformed payload. The point
        // is that a malformed payload no longer corrupts the value quietly.
        var bytes = new List<byte> { MsspWire.Iac, MsspWire.Sb, MsspWire.Mssp, MsspWire.Var };
        bytes.AddRange(Encoding.ASCII.GetBytes("NAME"));
        bytes.Add(MsspWire.Val);
        bytes.AddRange(Encoding.ASCII.GetBytes("a"));
        bytes.AddRange([MsspWire.Iac, MsspWire.Iac]);
        bytes.AddRange(Encoding.ASCII.GetBytes("b"));
        bytes.AddRange([MsspWire.Iac, MsspWire.Se]);

        var name = (await ReadRaw([.. bytes])).Name;

        await Assert.That(name).IsEqualTo("a\uFFFDb");
        await Assert.That(name!.Length).IsEqualTo(3).Because("the escaped byte must not vanish");
    }
}
