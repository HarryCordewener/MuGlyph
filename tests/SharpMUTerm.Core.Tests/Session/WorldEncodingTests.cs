using System.Text;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Session;
using SharpMUTerm.Core.Telnet;

namespace SharpMUTerm.Core.Tests.Session;

/// <summary>
/// How a world's <c>encoding</c> setting reaches the wire, and what a session reports about it.
/// </summary>
public class WorldEncodingTests
{
    private static readonly string[] AppOrder = { "utf-8", "iso-8859-1" };

    /// <summary>
    /// <c>auto</c> states the app-wide order and overrides nothing — the whole point. The app order
    /// was, until this change, a configuration field that no code path read at all.
    /// </summary>
    [Test]
    public async Task Auto_StatesTheAppsOrderAndOverridesNothing()
    {
        var (order, forced) = TelnetSessionOptions.ResolveWorldEncoding("auto", AppOrder);

        await Assert.That(forced).IsNull();
        await Assert.That(order.Select(e => e.WebName)).IsEquivalentTo(new[] { "utf-8", "iso-8859-1" });
    }

    /// <summary>An override is both: the head of the stated order <em>and</em> the encoding in force.</summary>
    [Test]
    public async Task ANamedEncoding_LeadsTheOrderAndAlsoOverrides()
    {
        var (order, forced) = TelnetSessionOptions.ResolveWorldEncoding("ISO-8859-1", AppOrder);

        await Assert.That(forced!.WebName).IsEqualTo("iso-8859-1");
        await Assert.That(order[0].WebName).IsEqualTo("iso-8859-1");
        await Assert.That(order.Select(e => e.WebName)).IsEquivalentTo(new[] { "iso-8859-1", "utf-8" });
    }

    /// <summary>
    /// A name this machine cannot resolve degrades to <c>auto</c> rather than refusing to connect: the
    /// F5 field takes anything the encoding provider might know, and a world whose encoding was
    /// renamed out from under it must still open.
    /// </summary>
    [Test]
    [Arguments("not-an-encoding")]
    [Arguments("")]
    [Arguments("   ")]
    public async Task AnUnresolvableName_BehavesAsAuto(string name)
    {
        var (order, forced) = TelnetSessionOptions.ResolveWorldEncoding(name, AppOrder);

        await Assert.That(forced).IsNull();
        await Assert.That(order[0].WebName).IsEqualTo("utf-8");
    }

    /// <summary>An app order naming nothing usable still leaves something to assume.</summary>
    [Test]
    public async Task AnUnusableAppOrder_FallsBackRatherThanLeavingNothingToAssume()
    {
        var (order, _) = TelnetSessionOptions.ResolveWorldEncoding("auto", new[] { "nonsense", "" });

        await Assert.That(order.Length).IsGreaterThan(0);
        await Assert.That(order[0].WebName).IsEqualTo("utf-8");
    }

    /// <summary>
    /// Before a connect a session reports no encoding at all. It has a configured <em>preference</em>
    /// and no fact, and the status row's whole bug was presenting the first as the second.
    /// </summary>
    [Test]
    public async Task ADisconnectedSession_ReportsNoEncoding()
    {
        var session = new WorldSession(new WorldDefinition { Name = "W", Host = "h", Port = 1 });

        await Assert.That(session.CurrentEncoding).IsNull();
        await session.DisposeAsync();
    }

    /// <summary>A connected session reports what its telnet layer is actually decoding with.</summary>
    [Test]
    public async Task AConnectedSession_ReportsWhatTheTelnetLayerIsUsing()
    {
        var telnet = new FakeTelnetSession
        {
            CurrentEncoding = new SessionEncoding(Encoding.Latin1, EncodingSource.Negotiated),
        };
        var session = new WorldSession(
            new WorldDefinition { Name = "W", Host = "h", Port = 1 },
            sessionFactory: _ => telnet);

        await session.ConnectAsync();

        await Assert.That(session.CurrentEncoding!.Value.Name).IsEqualTo("iso-8859-1");
        await Assert.That(session.CurrentEncoding!.Value.Source).IsEqualTo(EncodingSource.Negotiated);
        await session.DisposeAsync();
    }

    /// <summary>And forwards a change, so a live surface can repaint what it says.</summary>
    [Test]
    public async Task AChangeIsForwarded()
    {
        var telnet = new FakeTelnetSession();
        var session = new WorldSession(
            new WorldDefinition { Name = "W", Host = "h", Port = 1 },
            sessionFactory: _ => telnet);
        SessionEncoding? seen = null;
        session.EncodingChanged += (_, e) => seen = e.Encoding;

        await session.ConnectAsync();
        telnet.EmitEncoding(new SessionEncoding(Encoding.Latin1, EncodingSource.Negotiated));

        await Assert.That(seen!.Value.Name).IsEqualTo("iso-8859-1");
        await Assert.That(session.CurrentEncoding!.Value.Name).IsEqualTo("iso-8859-1");
        await session.DisposeAsync();
    }
}
