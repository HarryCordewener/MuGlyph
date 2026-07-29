using SharpMUTerm.Core.Diagnostics;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// What the ⌃P client message viewer draws. The log is explicitly a debugging aid, so each row has to
/// carry when and how loud as well as what — an undated list of sentences would not be one.
/// </summary>
public class ClientMessageRendererTests
{
    private static readonly DateTimeOffset Noon = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task EachRowCarriesItsTimeAndSeverity()
    {
        var rows = ClientMessageRenderer.Render(new[]
        {
            new ClientMessage(Noon, MessageSeverity.Warning, "⌃B nothing to split"),
            new ClientMessage(Noon.AddSeconds(5), MessageSeverity.Error, "could not connect"),
        });

        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows[0]).Contains(Noon.ToLocalTime().ToString("HH:mm:ss"));
        await Assert.That(rows[0]).Contains("warn");
        await Assert.That(rows[0]).Contains("nothing to split");
        await Assert.That(rows[1]).Contains("error");
        await Assert.That(rows[1]).Contains(ScreenPalette.Warn); // an error is drawn as one
    }

    /// <summary>An empty log says it is empty, rather than drawing a blank window that reads as broken.</summary>
    [Test]
    public async Task AnEmptyLogSaysSo()
    {
        var rows = ClientMessageRenderer.Render(Array.Empty<ClientMessage>());

        await Assert.That(rows).HasSingleItem();
        await Assert.That(rows[0]).Contains(ClientMessageRenderer.Empty);
    }

    /// <summary>A long message is clipped to the row and marked as clipped, not silently cut.</summary>
    [Test]
    public async Task ALongMessageIsClippedVisibly()
    {
        var rows = ClientMessageRenderer.Render(
            new[] { new ClientMessage(Noon, MessageSeverity.Info, new string('x', 200)) },
            width: 60);

        await Assert.That(rows[0]).Contains("…");
        await Assert.That(rows[0]).DoesNotContain(new string('x', 200));
    }

    /// <summary>The viewer sizes itself to its widest row, so the window is not built at a guess.</summary>
    [Test]
    public async Task MaxWidthCoversTheWidestMessage()
    {
        var entries = new[]
        {
            new ClientMessage(Noon, MessageSeverity.Info, "short"),
            new ClientMessage(Noon, MessageSeverity.Info, new string('y', 90)),
        };

        await Assert.That(ClientMessageRenderer.MaxWidth(entries)).IsGreaterThan(90);
        await Assert.That(ClientMessageRenderer.MaxWidth(Array.Empty<ClientMessage>()))
            .IsGreaterThanOrEqualTo(ClientMessageRenderer.Empty.Length);
    }
}
