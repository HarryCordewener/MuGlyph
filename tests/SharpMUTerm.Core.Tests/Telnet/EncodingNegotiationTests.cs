using System.Text;
using SharpMUTerm.Core.Telnet;

namespace SharpMUTerm.Core.Tests.Telnet;

/// <summary>
/// The encoding a session decodes with follows CHARSET (RFC 2066), and a world's configured encoding
/// is an <em>override</em> of that rather than a second opinion about it.
/// <para>
/// Two of these are regression pins rather than feature tests, and both were live bugs. The decode
/// path always followed <c>TelnetInterpreter.CurrentEncoding</c> — so the world's configured encoding
/// never decoded anything, and a server negotiating Latin-1 against a world configured UTF-8 silently
/// won — while that property <b>defaults to <see cref="Encoding.ASCII"/></b>, which is handed to the
/// byte and submit callbacks from the very first byte. So on the many MU* servers that never implement
/// CHARSET at all, every byte above 0x7F arrived as <c>?</c>: <c>café 日本語 🎲</c> read
/// <c>caf?? ????????? ????</c>. The status row meanwhile displayed the configured value as though it
/// were the one in force, which it was in neither case.
/// </para>
/// <para>
/// The awkward text is borrowed from the spill and password-leak suites' habit of using text that
/// breaks naive byte handling: an accent (one byte in Latin-1, two in UTF-8), CJK (three), and an
/// emoji (four, and a surrogate pair in UTF-16). A MU* client meets all of them.
/// </para>
/// </summary>
public class EncodingNegotiationTests
{
    private const byte IAC = 255;
    private const byte SB = 250;
    private const byte SE = 240;
    private const byte WILL = 251;
    private const byte CHARSET = 42;
    private const byte REQUEST = 1;

    /// <summary>Accent + CJK + astral-plane emoji: two, three and four UTF-8 bytes per character.</summary>
    private const string Awkward = "café 日本語 🎲";

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Drives a server that offers exactly <paramref name="charsets"/> when asked.</summary>
    private static async Task OfferCharsetsAsync(FakeTransport transport, params string[] charsets)
    {
        transport.FeedInbound(IAC, WILL, CHARSET);
        await Task.Delay(100).ConfigureAwait(false);

        var request = new List<byte> { IAC, SB, CHARSET, REQUEST };
        request.AddRange(Encoding.ASCII.GetBytes(";" + string.Join(";", charsets)));
        request.AddRange(new byte[] { IAC, SE });
        transport.FeedInbound(request.ToArray());
        await Task.Delay(200).ConfigureAwait(false);
    }

    private static async Task<string?> FirstLineAsync(TelnetSession session, FakeTransport transport, byte[] line)
    {
        string? seen = null;
        session.OutputReceived += (_, e) => seen ??= e.Text;
        transport.FeedInbound(line);

        var deadline = Environment.TickCount64 + 3000;
        while (seen is null && Environment.TickCount64 < deadline)
        {
            await Task.Delay(15).ConfigureAwait(false);
        }

        return seen;
    }

    // ---- auto -------------------------------------------------------------------------------------

    /// <summary>
    /// <c>auto</c>: the client states UTF-8 first, the server offers only Latin-1, and the session
    /// adopts Latin-1 — for what it reports <em>and</em> for what it decodes. Nothing here is
    /// configured to Latin-1; it is entirely the negotiated result.
    /// </summary>
    [Test]
    public async Task Auto_AdoptsWhateverCharsetNegotiationSettledOn()
    {
        var transport = new FakeTransport();
        await using var session = new TelnetSession(transport, options: AutoOptions());
        await session.ConnectAsync();

        await OfferCharsetsAsync(transport, "ISO-8859-1");

        var text = await FirstLineAsync(session, transport, Encoding.Latin1.GetBytes("café\r\n"));

        await Assert.That(session.CurrentEncoding.Name).IsEqualTo("iso-8859-1");
        await Assert.That(session.CurrentEncoding.Source).IsEqualTo(EncodingSource.Negotiated);
        await Assert.That(text).IsEqualTo("café");
        await session.DisconnectAsync();
    }

    /// <summary>And UTF-8, when that is what the server picks, carries the astral-plane cases too.</summary>
    [Test]
    public async Task Auto_NegotiatedUtf8_DecodesCjkAndEmoji()
    {
        var transport = new FakeTransport();
        await using var session = new TelnetSession(transport, options: AutoOptions());
        await session.ConnectAsync();

        await OfferCharsetsAsync(transport, "UTF-8", "ISO-8859-1");

        var text = await FirstLineAsync(session, transport, Utf8NoBom.GetBytes(Awkward + "\r\n"));

        await Assert.That(session.CurrentEncoding.Source).IsEqualTo(EncodingSource.Negotiated);
        await Assert.That(session.CurrentEncoding.Name).IsEqualTo("utf-8");
        await Assert.That(text).IsEqualTo(Awkward);
        await session.DisconnectAsync();
    }

    /// <summary>
    /// The no-CHARSET case, which is most MU* servers: nothing negotiates, so the head of the stated
    /// preference order is assumed, and it is <b>not</b> the interpreter's ASCII default. This is the
    /// pin for the bug — before it, this exact line came back as <c>caf?? ????????? ????</c>.
    /// </summary>
    [Test]
    public async Task Auto_ServerNeverNegotiates_AssumesTheHeadOfTheOrder_NotAscii()
    {
        var transport = new FakeTransport();
        await using var session = new TelnetSession(transport, options: AutoOptions());
        await session.ConnectAsync();

        var text = await FirstLineAsync(session, transport, Utf8NoBom.GetBytes(Awkward + "\r\n"));

        await Assert.That(session.CurrentEncoding.Name).IsEqualTo("utf-8");
        await Assert.That(session.CurrentEncoding.Source).IsEqualTo(EncodingSource.Assumed);
        await Assert.That(text).IsEqualTo(Awkward);
        await Assert.That(text).DoesNotContain("?");
        await session.DisconnectAsync();
    }

    /// <summary>
    /// The assumption follows the app's own order rather than a hard-coded UTF-8: an app whose first
    /// preference is Latin-1 assumes Latin-1 when nothing negotiates.
    /// </summary>
    [Test]
    public async Task Auto_ServerNeverNegotiates_AssumesTheAppsFirstPreference()
    {
        var transport = new FakeTransport();
        await using var session = new TelnetSession(
            transport,
            options: new TelnetSessionOptions
            {
                CharsetOrder = TelnetSessionOptions.PreferEncoding(null, new[] { "iso-8859-1", "utf-8" }),
            });
        await session.ConnectAsync();

        var text = await FirstLineAsync(session, transport, Encoding.Latin1.GetBytes("café\r\n"));

        await Assert.That(session.CurrentEncoding.Name).IsEqualTo("iso-8859-1");
        await Assert.That(session.CurrentEncoding.Source).IsEqualTo(EncodingSource.Assumed);
        await Assert.That(text).IsEqualTo("café");
        await session.DisconnectAsync();
    }

    // ---- override ---------------------------------------------------------------------------------

    /// <summary>
    /// An override outranks a negotiated result that differs from it. The server here agrees to UTF-8
    /// (the only charset it offers, and one we do list), and the session still decodes Latin-1 —
    /// which is the whole meaning of the word: the user has told the client what the bytes are.
    /// </summary>
    [Test]
    public async Task Override_WinsOverADifferingNegotiatedEncoding()
    {
        var transport = new FakeTransport();
        await using var session = new TelnetSession(transport, options: OverrideOptions("ISO-8859-1"));
        await session.ConnectAsync();

        await OfferCharsetsAsync(transport, "UTF-8");

        // Latin-1 bytes for "café" — invalid UTF-8, so a session that had followed the negotiation
        // would produce a replacement character here rather than the text.
        var text = await FirstLineAsync(session, transport, Encoding.Latin1.GetBytes("café\r\n"));

        await Assert.That(session.CurrentEncoding.Name).IsEqualTo("iso-8859-1");
        await Assert.That(session.CurrentEncoding.Source).IsEqualTo(EncodingSource.Override);
        await Assert.That(text).IsEqualTo("café");
        await session.DisconnectAsync();
    }

    /// <summary>
    /// The override is still <em>offered</em>, at the head of the order, so a server that speaks RFC
    /// 2066 agrees to the thing we are going to use anyway rather than being left to guess. Declining
    /// negotiation outright would tell the server nothing about what we can read.
    /// </summary>
    [Test]
    public async Task Override_IsStillOfferedAtTheHeadOfTheNegotiatedOrder()
    {
        var transport = new FakeTransport();
        await using var session = new TelnetSession(transport, options: OverrideOptions("ISO-8859-1"));
        await session.ConnectAsync();

        // Server asks us for our list: IAC WILL CHARSET → we DO → it REQUESTs, or (as here) it asks us
        // to send ours by accepting our WILL. Either way the list we send names our charsets in order.
        transport.FeedInbound(IAC, 253, CHARSET); // IAC DO CHARSET
        await Task.Delay(300);

        var sent = Encoding.ASCII.GetString(transport.SentBytes);
        var offered = sent[sent.IndexOf(";iso-8859-1", StringComparison.OrdinalIgnoreCase)..];

        await Assert.That(sent).Contains(";iso-8859-1");
        await Assert.That(offered).Contains("utf-8"); // the app order follows it, as fallbacks
        await session.DisconnectAsync();
    }

    /// <summary>An override with no negotiation at all is still the override, not the assumption.</summary>
    [Test]
    public async Task Override_ServerNeverNegotiates_IsStillReportedAsAnOverride()
    {
        var transport = new FakeTransport();
        await using var session = new TelnetSession(transport, options: OverrideOptions("ISO-8859-1"));
        await session.ConnectAsync();

        var text = await FirstLineAsync(session, transport, Encoding.Latin1.GetBytes("café\r\n"));

        await Assert.That(session.CurrentEncoding.Source).IsEqualTo(EncodingSource.Override);
        await Assert.That(text).IsEqualTo("café");
        await session.DisconnectAsync();
    }

    /// <summary>
    /// What the session sends is encoded with the same encoding it decodes with. A world pinned to
    /// Latin-1 that typed <c>café</c> used to put the ASCII default's <c>?</c> on the wire.
    /// </summary>
    [Test]
    public async Task Override_EncodesOutboundTextWithTheEncodingInForce()
    {
        var transport = new FakeTransport();
        await using var session = new TelnetSession(transport, options: OverrideOptions("ISO-8859-1"));
        await session.ConnectAsync();

        await session.SendLineAsync("say café");

        var deadline = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < deadline &&
               !Encoding.Latin1.GetString(transport.SentBytes).Contains("say café"))
        {
            await Task.Delay(15);
        }

        await Assert.That(Encoding.Latin1.GetString(transport.SentBytes)).Contains("say café\r\n");
        await Assert.That(transport.SentBytes).Contains((byte)0xE9); // é in Latin-1, not '?'
        await session.DisconnectAsync();
    }

    /// <summary>
    /// The same for <c>auto</c> on a silent server: outbound text is UTF-8 because that is what is
    /// assumed inbound. One session, one encoding, both directions.
    /// </summary>
    [Test]
    public async Task Auto_EncodesOutboundTextWithTheAssumedEncoding()
    {
        var transport = new FakeTransport();
        await using var session = new TelnetSession(transport, options: AutoOptions());
        await session.ConnectAsync();

        await session.SendLineAsync(Awkward);

        var deadline = Environment.TickCount64 + 3000;
        while (Environment.TickCount64 < deadline && transport.SentBytes.Length < Awkward.Length)
        {
            await Task.Delay(15);
        }

        await Assert.That(Utf8NoBom.GetString(transport.SentBytes)).Contains(Awkward);
        await session.DisconnectAsync();
    }

    // ---- observation ------------------------------------------------------------------------------

    /// <summary>
    /// The negotiated result is <em>observed</em>. Nothing in this client used to look at it: there was
    /// no subscriber to CHARSET's callback and nothing read the interpreter's encoding, so the client
    /// stated a preference and never learned the answer.
    /// </summary>
    [Test]
    public async Task EncodingChanged_ReportsTheNegotiatedResultOnce()
    {
        var transport = new FakeTransport();
        await using var session = new TelnetSession(transport, options: AutoOptions());
        var changes = new List<SessionEncoding>();
        session.EncodingChanged += (_, e) =>
        {
            lock (changes)
            {
                changes.Add(e.Encoding);
            }
        };
        await session.ConnectAsync();

        await OfferCharsetsAsync(transport, "ISO-8859-1");
        await Task.Delay(200);

        SessionEncoding[] observed;
        lock (changes)
        {
            observed = changes.ToArray();
        }

        // Exactly one: the two observation arms (CHARSET's own callback and the post-batch reconcile
        // that covers the direction the library does not signal) must not double-report.
        await Assert.That(observed).HasSingleItem();
        await Assert.That(observed[0].Name).IsEqualTo("iso-8859-1");
        await Assert.That(observed[0].Source).IsEqualTo(EncodingSource.Negotiated);

        await session.DisconnectAsync();
    }

    /// <summary>
    /// An override does not move, so nothing is reported when a server agrees to something else: the
    /// encoding in force never changed, and an event saying it had would be the status row's old lie
    /// in a new place.
    /// </summary>
    [Test]
    public async Task EncodingChanged_IsSilentUnderAnOverride()
    {
        var transport = new FakeTransport();
        await using var session = new TelnetSession(transport, options: OverrideOptions("ISO-8859-1"));
        var changes = 0;
        session.EncodingChanged += (_, _) => Interlocked.Increment(ref changes);
        await session.ConnectAsync();

        await OfferCharsetsAsync(transport, "UTF-8");
        await Task.Delay(200);

        await Assert.That(Volatile.Read(ref changes)).IsEqualTo(0);
        await session.DisconnectAsync();
    }

    /// <summary>
    /// The library decodes its own out-of-band payloads with the interpreter's encoding, which is the
    /// same <see cref="Encoding.ASCII"/> default. Seeding it means a GMCP message carrying non-ASCII
    /// text survives a server that has not negotiated CHARSET — it used to arrive full of <c>?</c>.
    /// </summary>
    [Test]
    public async Task GmcpPayloads_DecodeWithTheAssumedEncodingBeforeAnyNegotiation()
    {
        const byte GMCP = 201;
        var transport = new FakeTransport();
        await using var session = new TelnetSession(transport, options: AutoOptions());
        string? json = null;
        session.GmcpReceived += (_, e) => json ??= e.Json;
        await session.ConnectAsync();

        var message = new List<byte> { IAC, SB, GMCP };
        message.AddRange(Utf8NoBom.GetBytes("Room.Info {\"name\":\"café 日本語\"}"));
        message.AddRange(new byte[] { IAC, SE });
        transport.FeedInbound(message.ToArray());

        var deadline = Environment.TickCount64 + 3000;
        while (json is null && Environment.TickCount64 < deadline)
        {
            await Task.Delay(15);
        }

        await Assert.That(json).IsNotNull();
        await Assert.That(json!).Contains("café 日本語");
        await session.DisconnectAsync();
    }

    /// <summary>Before a connect there is no interpreter, and the assumption is all there is to report.</summary>
    [Test]
    public async Task BeforeConnecting_TheAssumptionIsWhatIsReported()
    {
        await using var session = new TelnetSession(new FakeTransport(), options: AutoOptions());

        await Assert.That(session.CurrentEncoding.Name).IsEqualTo("utf-8");
        await Assert.That(session.CurrentEncoding.Source).IsEqualTo(EncodingSource.Assumed);
    }

    // ---- labels -----------------------------------------------------------------------------------

    /// <summary>
    /// The status row's cell. Unqualified means the server agreed; the two exceptions each cost one
    /// short word, because a user chasing mojibake has to be able to tell whose decision this was.
    /// </summary>
    [Test]
    public async Task Label_QualifiesEverythingExceptANegotiatedResult()
    {
        await Assert.That(new SessionEncoding(Utf8NoBom, EncodingSource.Negotiated).Label).IsEqualTo("utf-8");
        await Assert.That(new SessionEncoding(Utf8NoBom, EncodingSource.Assumed).Label).IsEqualTo("utf-8 assumed");
        await Assert.That(new SessionEncoding(Encoding.Latin1, EncodingSource.Override).Label)
            .IsEqualTo("iso-8859-1 forced");
    }

    private static TelnetSessionOptions AutoOptions() => new()
    {
        CharsetOrder = TelnetSessionOptions.PreferEncoding(TelnetSessionOptions.AutoEncodingName),
        EncodingOverride = TelnetSessionOptions.ResolveEncoding(TelnetSessionOptions.AutoEncodingName),
    };

    private static TelnetSessionOptions OverrideOptions(string name) => new()
    {
        CharsetOrder = TelnetSessionOptions.PreferEncoding(name),
        EncodingOverride = TelnetSessionOptions.ResolveEncoding(name),
    };
}
