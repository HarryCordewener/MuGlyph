using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Tests.Text;

public class ScrollbackBufferTests
{
    private static StyledLine Line(string text) => StyledLine.FromText(text, TextStyle.Default);

    [Test]
    public async Task Append_IncreasesCount()
    {
        var buffer = new ScrollbackBuffer();
        buffer.Append(Line("a"));
        buffer.Append(Line("b"));
        await Assert.That(buffer.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Capacity_EvictsOldestLines()
    {
        var buffer = new ScrollbackBuffer(capacity: 3);
        for (var i = 0; i < 5; i++)
        {
            buffer.Append(Line(i.ToString()));
        }

        await Assert.That(buffer.Count).IsEqualTo(3);
        var snapshot = buffer.Snapshot();
        await Assert.That(snapshot[0].Text).IsEqualTo("2");
        await Assert.That(snapshot[2].Text).IsEqualTo("4");
    }

    [Test]
    public async Task LinesAppended_FiresWithAppendedLines()
    {
        var buffer = new ScrollbackBuffer();
        IReadOnlyList<StyledLine>? received = null;
        buffer.LinesAppended += (_, e) => received = e.Lines;
        buffer.Append(Line("x"));
        await Assert.That(received).IsNotNull();
        await Assert.That(received!).HasSingleItem();
        await Assert.That(received![0].Text).IsEqualTo("x");
    }

    [Test]
    public async Task AppendRange_RaisesSingleEvent()
    {
        var buffer = new ScrollbackBuffer();
        var events = 0;
        buffer.LinesAppended += (_, _) => events++;
        buffer.AppendRange(new[] { Line("a"), Line("b"), Line("c") });
        await Assert.That(events).IsEqualTo(1);
        await Assert.That(buffer.Count).IsEqualTo(3);
    }

    [Test]
    public async Task GetRange_ReturnsRequestedWindow()
    {
        var buffer = new ScrollbackBuffer();
        for (var i = 0; i < 10; i++)
        {
            buffer.Append(Line(i.ToString()));
        }

        var range = buffer.GetRange(3, 4);
        await Assert.That(range).Count().IsEqualTo(4);
        await Assert.That(range[0].Text).IsEqualTo("3");
        await Assert.That(range[3].Text).IsEqualTo("6");
    }

    [Test]
    public async Task GetRange_ClampsPastEnd()
    {
        var buffer = new ScrollbackBuffer();
        buffer.AppendRange(new[] { Line("a"), Line("b") });
        var range = buffer.GetRange(1, 10);
        await Assert.That(range).HasSingleItem();
        await Assert.That(range[0].Text).IsEqualTo("b");
    }

    [Test]
    public async Task Clear_RemovesAll()
    {
        var buffer = new ScrollbackBuffer();
        buffer.AppendRange(new[] { Line("a"), Line("b") });
        buffer.Clear();
        await Assert.That(buffer.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Constructor_RejectsNonPositiveCapacity()
    {
        await Assert.That(() => new ScrollbackBuffer(0)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Indices_AreAbsoluteAndSurviveEviction()
    {
        var buffer = new ScrollbackBuffer(capacity: 4);
        for (var i = 0; i < 10; i++)
        {
            buffer.Append(Line(i.ToString()));
        }

        await Assert.That(buffer.TotalLines).IsEqualTo(10L);
        await Assert.That(buffer.OldestIndex).IsEqualTo(6L);
        await Assert.That(buffer.AvailableLines).IsEqualTo(4L);

        // Index 7 still means the eighth line ever seen, not "the second one still held".
        await Assert.That(buffer.GetRange(7, 1)[0].Text).IsEqualTo("7");
    }

    [Test]
    public async Task GetTail_ReturnsTheNewestLines()
    {
        var buffer = new ScrollbackBuffer(capacity: 100);
        for (var i = 0; i < 10; i++)
        {
            buffer.Append(Line(i.ToString()));
        }

        await Assert.That(buffer.GetTail(3).Select(l => l.Text)).IsEquivalentTo(new[] { "7", "8", "9" });
        await Assert.That(buffer.GetTail(100)).Count().IsEqualTo(10);
        await Assert.That(new ScrollbackBuffer(capacity: 4).GetTail(3)).IsEmpty();
    }

    [Test]
    public async Task RingGrowth_KeepsOrderAcrossReallocationAndWraparound()
    {
        // Capacity 100 with a ring that starts at 16 and doubles: appending 250 lines reallocates
        // several times and then wraps, and the order has to survive both.
        var buffer = new ScrollbackBuffer(capacity: 100);
        for (var i = 0; i < 250; i++)
        {
            buffer.Append(Line(i.ToString()));
        }

        var snapshot = buffer.Snapshot();
        await Assert.That(snapshot).Count().IsEqualTo(100);
        for (var i = 0; i < 100; i++)
        {
            await Assert.That(snapshot[i].Text).IsEqualTo((150 + i).ToString());
        }

        await Assert.That(buffer.GetRange(150, 100).Select(l => l.Text)).IsEquivalentTo(snapshot.Select(l => l.Text));
    }

    [Test]
    public async Task MemoryOnlyBuffer_IsNotSpilling()
    {
        var buffer = new ScrollbackBuffer(capacity: 2);
        buffer.AppendRange(new[] { Line("a"), Line("b"), Line("c") });
        await Assert.That(buffer.IsSpilling).IsFalse();
        await Assert.That(buffer.SpilledLines).IsEqualTo(0L);
    }
}
