using System.Text;
using SharpMUTerm.Core.Telnet.Mssp;
using SharpMUTerm.Core.Transport;
using SharpMUTerm.Crawler.Model;
using SharpMUTerm.Crawler.Probing;
using SharpMUTerm.Crawler.Tests.Support;

namespace SharpMUTerm.Crawler.Tests;

/// <summary>
/// The probe end to end: a real <c>TelnetSession</c> over a scripted server, negotiating for real.
/// </summary>
public class ProbeTests
{
    private static MsspHost Host(string name = "server.example.org", int port = 4201) =>
        MsspHost.Create(name, port)!;

    private static CrawlOptions Options => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        MsspTimeout = TimeSpan.FromSeconds(5),
    };

    private static ScriptedTransport MsspServer(params string[] referrals) =>
        new ScriptedTransport { Greeting = MsspWire.Offer() }
            .RespondingToDo(MsspWire.Mssp, MsspWire.Subnegotiation(MsspWire.RepresentativeReport(referrals)));

    [Test]
    public async Task AServerThatOffersMsspIsReadCompletely()
    {
        var transport = MsspServer("peer.example.net 4000", "2001:db8::5 4201");
        var probe = new TelnetMsspProbe(Options, _ => transport);

        var result = await probe.ProbeAsync(Host(), CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(CrawlOutcome.MsspReceived);
        await Assert.That(result.Data).IsNotNull();

        var data = result.Data!;
        await Assert.That(data.Source).IsEqualTo(MsspSource.Wire);
        await Assert.That(data.Name).IsEqualTo("Corvid Nest");
        await Assert.That(data.Players).IsEqualTo(17);

        // The array-valued variables that the telnet library's own model destroys.
        await Assert.That(data.Ports).IsEquivalentTo(new[] { 80, 23, 4201 });
        await Assert.That(data.Referrals.Select(r => r.ToReferralString()))
            .IsEquivalentTo(new[] { "peer.example.net 4000", "2001:db8::5 4201" });

        // The booleans and the unknown variables, likewise.
        await Assert.That(data.Flag("ANSI")).IsTrue();
        await Assert.That(data.Default("CORVID SPECIFIC")).IsEqualTo("nevermore");
    }

    [Test]
    public async Task AServerThatNegotiatesNoMsspAtAllIsRecordedAsSuchRatherThanAsAFailure()
    {
        // Most MU* servers. It offers ECHO and SUPPRESS-GO-AHEAD, prints a login banner, and never
        // mentions MSSP.
        var banner = new List<byte>();
        banner.AddRange([MsspWire.Iac, MsspWire.Will, 1]);   // WILL ECHO
        banner.AddRange([MsspWire.Iac, MsspWire.Will, 3]);   // WILL SUPPRESS-GO-AHEAD
        banner.AddRange(Encoding.ASCII.GetBytes("\r\nWelcome to Some MUD.\r\nBy what name do you wish to be known? "));

        var transport = new ScriptedTransport { Greeting = [.. banner] };
        var options = Options with { MsspTimeout = TimeSpan.FromMilliseconds(300) };

        var result = await new TelnetMsspProbe(options, _ => transport).ProbeAsync(Host(), CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(CrawlOutcome.NoMssp);
        await Assert.That(result.Data).IsNull();
        await Assert.That(result.IsFailure).IsFalse();
    }

    [Test]
    public async Task AServerThatOnlyAnswersWhenAskedIsStillRead()
    {
        // The case that made a live server look as if it had no MSSP at all. The specification says a
        // server "should" send IAC WILL MSSP on connect; plenty do not, and answer IAC DO MSSP instead.
        // This server volunteers nothing — no greeting whatsoever — so it is only reachable by asking.
        var transport = new ScriptedTransport()
            .RespondingToDo(MsspWire.Mssp, MsspWire.Subnegotiation(("NAME", ["Silent Until Asked"])));

        var result = await new TelnetMsspProbe(Options, _ => transport).ProbeAsync(Host(), CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(CrawlOutcome.MsspReceived);
        await Assert.That(result.Data!.Name).IsEqualTo("Silent Until Asked");
    }

    [Test]
    public async Task TheProbeAsksForMsspRatherThanWaitingToBeOffered()
    {
        var transport = new ScriptedTransport();
        var probing = new TelnetMsspProbe(
            Options with { MsspTimeout = TimeSpan.FromMilliseconds(400) }, _ => transport)
            .ProbeAsync(Host(), CancellationToken.None);

        await Assert.That(await transport.WaitForSentAsync([MsspWire.Iac, MsspWire.Do, MsspWire.Mssp]))
            .IsTrue()
            .Because("a crawler that only listens misses every server that waits to be asked");

        transport.Close();
        await probing;
    }

    [Test]
    public async Task AServerThatHangsUpWithoutMsspIsRecordedAsHavingNone()
    {
        var transport = new ScriptedTransport();
        var probe = new TelnetMsspProbe(Options, _ => transport);

        var probing = probe.ProbeAsync(Host(), CancellationToken.None);
        transport.Close();

        await Assert.That((await probing).Outcome).IsEqualTo(CrawlOutcome.NoMssp);
    }

    [Test]
    public async Task NothingButTelnetNegotiationIsEverSentToTheServer()
    {
        // The politeness requirement, asserted against the bytes rather than against a reading of the
        // code: a crawler must never log in and never send a command. Every byte the probe puts on the
        // wire has to belong to an IAC sequence.
        var transport = MsspServer("peer.example.net 4000");
        var result = await new TelnetMsspProbe(Options, _ => transport).ProbeAsync(Host(), CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(CrawlOutcome.MsspReceived);

        var stray = StrayDataBytes(transport.Sent);
        await Assert.That(stray)
            .IsEmpty()
            .Because($"the crawler sent application data: \"{Encoding.ASCII.GetString([.. stray])}\"");
    }

    [Test]
    public async Task NothingIsSentEvenToAServerThatPromptsForALogin()
    {
        // The case that would tempt a client: a login banner arrives and the connection sits there. It
        // must still be answered with silence.
        var banner = Encoding.ASCII.GetBytes("\r\nEnter your name: ");
        var transport = new ScriptedTransport { Greeting = banner };
        var options = Options with { MsspTimeout = TimeSpan.FromMilliseconds(300) };

        await new TelnetMsspProbe(options, _ => transport).ProbeAsync(Host(), CancellationToken.None);

        await Assert.That(StrayDataBytes(transport.Sent)).IsEmpty();
    }

    [Test]
    public async Task TheCrawlerNamesItselfWhenTheServerAsksWhatConnected()
    {
        // MTTS: the server sends IAC SB TTYPE SEND IAC SE and the client answers IAC SB TTYPE IS <name>.
        // A crawler that did not answer with its own name would be logged as whatever the telnet library
        // calls itself, which tells a server operator nothing.
        const byte ttype = 24;
        const byte send = 1;

        var transport = new ScriptedTransport { Greeting = [MsspWire.Iac, MsspWire.Do, ttype] };

        var probing = new TelnetMsspProbe(
            Options with { MsspTimeout = TimeSpan.FromSeconds(3) },
            _ => transport).ProbeAsync(Host(), CancellationToken.None);

        // Wait for the client to agree to TTYPE, then ask it for one.
        await Assert.That(await transport.WaitForSentAsync([MsspWire.Iac, MsspWire.Will, ttype])).IsTrue();
        transport.SendToClient(MsspWire.Iac, MsspWire.Sb, ttype, send, MsspWire.Iac, MsspWire.Se);

        await Assert.That(await transport.WaitForSentAsync(Encoding.ASCII.GetBytes("SHARPMUTERM-MSSPCRAWLER")))
            .IsTrue();

        transport.Close();
        await probing;

        var text = Encoding.ASCII.GetString(transport.Sent);
        await Assert.That(text).Contains("SHARPMUTERM-MSSPCRAWLER");
        await Assert.That(text).DoesNotContain("TNC");
    }

    [Test]
    public async Task AConnectionThatIsRefusedIsRecordedAsAFailure()
    {
        var result = await new TelnetMsspProbe(Options, _ => new RefusingTransport())
            .ProbeAsync(Host(), CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(CrawlOutcome.ConnectFailed);
        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).IsEqualTo("ConnectionRefused");
    }

    /// <summary>
    /// Every byte written that is not part of an <c>IAC</c> sequence — i.e. everything that would be
    /// application data reaching the game.
    /// </summary>
    private static List<byte> StrayDataBytes(byte[] sent)
    {
        var stray = new List<byte>();
        var i = 0;
        while (i < sent.Length)
        {
            if (sent[i] != MsspWire.Iac)
            {
                stray.Add(sent[i]);
                i++;
                continue;
            }

            if (i + 1 >= sent.Length)
            {
                break;
            }

            var command = sent[i + 1];
            if (command == MsspWire.Sb)
            {
                // Skip to IAC SE, honouring IAC IAC inside the payload.
                i += 2;
                while (i + 1 < sent.Length && !(sent[i] == MsspWire.Iac && sent[i + 1] == MsspWire.Se))
                {
                    i += sent[i] == MsspWire.Iac && sent[i + 1] == MsspWire.Iac ? 2 : 1;
                }

                i += 2;
                continue;
            }

            i += command is >= MsspWire.Will and <= MsspWire.Dont ? 3 : 2;
        }

        return stray;
    }

    private sealed class RefusingTransport : ITransport
    {
        public bool IsConnected => false;

        public string? RemoteDescription => null;

        public Task ConnectAsync(CancellationToken cancellationToken = default) =>
            throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused);

        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(0);

        public Task CloseAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
