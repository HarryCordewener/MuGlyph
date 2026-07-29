using SharpMUTerm.Core.Telnet;
using TelnetNegotiationCore.Interpreters;

namespace SharpMUTerm.Core.Tests.Telnet;

/// <summary>
/// A world's <c>keepalive</c> seconds (F5) becomes the idle window the telnet session sends
/// <c>IAC NOP</c> on. Before this it picked a figure in the status bar and nothing else — there was
/// no keepalive at all, in either direction.
/// <para>
/// The resolver clamps rather than throws, because this value arrives from a config file that may
/// predate the library's bounds or have been hand-edited. Refusing to connect over an out-of-range
/// keepalive would be a worse answer than keeping the connection alive a little more or less often
/// than the file asked for.
/// </para>
/// </summary>
public class KeepaliveIntervalTests
{
    [Test]
    public async Task AConfiguredIntervalIsUsedAsGiven()
    {
        await Assert.That(TelnetSessionOptions.ResolveKeepalive(90)).IsEqualTo(TimeSpan.FromSeconds(90));
    }

    /// <summary>Zero is how the config spells "off", and it is the default for a new world.</summary>
    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(int.MinValue)]
    public async Task ZeroOrNegativeMeansNoKeepalive(int seconds)
    {
        await Assert.That(TelnetSessionOptions.ResolveKeepalive(seconds)).IsNull();
    }

    /// <summary>
    /// The smallest keepalive that isn't "off" already satisfies the library's minimum, because that
    /// minimum is one second and this setting is a whole number of them. This pins the property the
    /// resolver relies on rather than the clamp it therefore doesn't need.
    /// </summary>
    [Test]
    public async Task TheSmallestKeepaliveThatIsNotOffMeetsTheLibrarysMinimum()
    {
        await Assert.That(TelnetInterpreter.MinimumKeepAliveInterval)
            .IsLessThanOrEqualTo(TimeSpan.FromSeconds(1));

        await Assert.That(TelnetSessionOptions.ResolveKeepalive(1)).IsEqualTo(TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Clamped against the library's own constant rather than a number copied here, so the two cannot
    /// drift apart if the library ever moves it.
    /// </summary>
    [Test]
    public async Task AnIntervalAboveTheLibrarysMaximumIsClampedDownToIt()
    {
        var resolved = TelnetSessionOptions.ResolveKeepalive(int.MaxValue);

        await Assert.That(resolved).IsEqualTo(TelnetInterpreter.MaximumKeepAliveInterval);
    }

    /// <summary>
    /// Whatever the resolver returns must be something the library will actually accept — this is the
    /// assertion that matters, since the clamp exists precisely so a builder never throws at connect.
    /// </summary>
    [Test]
    [Arguments(1)]
    [Arguments(30)]
    [Arguments(3600)]
    [Arguments(int.MaxValue)]
    public async Task AnyResolvedIntervalIsInsideTheLibrarysAcceptedRange(int seconds)
    {
        var resolved = TelnetSessionOptions.ResolveKeepalive(seconds);

        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Value).IsGreaterThanOrEqualTo(TelnetInterpreter.MinimumKeepAliveInterval);
        await Assert.That(resolved.Value).IsLessThanOrEqualTo(TelnetInterpreter.MaximumKeepAliveInterval);
    }
}
