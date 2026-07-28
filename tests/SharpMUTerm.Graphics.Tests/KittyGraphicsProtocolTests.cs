using SharpMUTerm.Core.Text;
using SharpMUTerm.Graphics;

namespace SharpMUTerm.Graphics.Tests;

public class KittyGraphicsProtocolTests
{
    private const string Esc = "\u001b";
    private const string ApcStart = Esc + "_G";
    private const string St = Esc + "\\";

    private readonly KittyGraphicsProtocol _kitty = new();

    [Test]
    public async Task TransmitAndDisplay_TinyRgba_GoldenOutput()
    {
        // base64 of {1,2,3,4} is "AQIDBA==".
        var payload = new byte[] { 1, 2, 3, 4 };
        var result = _kitty.TransmitAndDisplay(1, payload, 1, 1, KittyImageFormat.Rgba);

        var expected = ApcStart + "a=T,f=32,i=1,s=1,v=1,m=0;AQIDBA==" + St;
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task TransmitAndDisplay_Png_UsesFormat100()
    {
        var payload = new byte[] { 0xAA };
        var result = _kitty.TransmitAndDisplay(9, payload, 4, 5, KittyImageFormat.Png);

        var expected = ApcStart + "a=T,f=100,i=9,s=4,v=5,m=0;qg==" + St;
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task TransmitAndDisplay_ExactlyOneChunk_When4096Base64()
    {
        // 3072 zero bytes -> base64 length exactly 4096 -> single m=0 chunk.
        var payload = new byte[3072];
        var result = _kitty.TransmitAndDisplay(2, payload, 1, 1, KittyImageFormat.Rgba);

        // Only one APC block (one terminator).
        await Assert.That(CountOccurrences(result, St)).IsEqualTo(1);
        await Assert.That(result).Contains(",m=0;");
        await Assert.That(result).DoesNotContain(",m=1;");
    }

    [Test]
    public async Task TransmitAndDisplay_TwoChunks_AtBoundary()
    {
        // 3075 zero bytes -> base64 length 4100 -> chunk0 (4096, m=1) + chunk1 (4, m=0).
        var payload = new byte[3075];
        var result = _kitty.TransmitAndDisplay(3, payload, 1, 1, KittyImageFormat.Rgba);

        // Two APC blocks.
        await Assert.That(CountOccurrences(result, ApcStart)).IsEqualTo(2);
        await Assert.That(CountOccurrences(result, St)).IsEqualTo(2);

        // First chunk carries controls + m=1; continuation carries only m=0.
        await Assert.That(result).Contains("a=T,f=32,i=3,s=1,v=1,m=1;");
        await Assert.That(result).Contains(St + ApcStart + "m=0;");

        // The first chunk's base64 payload is exactly 4096 chars (all 'A' for zero bytes).
        var firstPayloadStart = result.IndexOf(",m=1;", StringComparison.Ordinal) + ",m=1;".Length;
        var firstPayloadEnd = result.IndexOf(St, StringComparison.Ordinal);
        await Assert.That(firstPayloadEnd - firstPayloadStart).IsEqualTo(4096);
    }

    [Test]
    public async Task Delete_GoldenOutput()
    {
        var result = _kitty.Delete(5);
        await Assert.That(result).IsEqualTo(ApcStart + "a=d,i=5;" + St);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task TransmitAndDisplay_NonPositiveId_Throws(int id)
    {
        await Assert.That(() => _kitty.TransmitAndDisplay(id, new byte[] { 1 }, 1, 1, KittyImageFormat.Rgba))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task BuildPlaceholder_HasCorrectDimensions()
    {
        var rows = _kitty.BuildPlaceholder(1, cols: 3, rows: 2);
        await Assert.That(rows).Count().IsEqualTo(2);
        foreach (var line in rows)
        {
            await Assert.That(line.Spans).Count().IsEqualTo(3);
        }
    }

    [Test]
    public async Task BuildPlaceholder_CellCarriesPlaceholderRuneAndDiacritics()
    {
        var rows = _kitty.BuildPlaceholder(1, cols: 2, rows: 2);

        var cell = rows[1].Spans[0].Text; // row 1, col 0
        var expected =
            char.ConvertFromUtf32(KittyGraphicsProtocol.PlaceholderCodePoint) +
            char.ConvertFromUtf32(KittyGraphicsProtocol.RowColumnDiacritics[1]) +
            char.ConvertFromUtf32(KittyGraphicsProtocol.RowColumnDiacritics[0]);
        await Assert.That(cell).IsEqualTo(expected);
    }

    [Test]
    public async Task BuildPlaceholder_ImageIdEncodedInForeground()
    {
        // id 0x010203 -> R=1, G=2, B=3.
        var rows = _kitty.BuildPlaceholder(0x010203, cols: 1, rows: 1);
        var fg = rows[0].Spans[0].Style.Foreground;

        await Assert.That(fg.Kind).IsEqualTo(TerminalColorKind.Rgb);
        await Assert.That(fg.R).IsEqualTo((byte)1);
        await Assert.That(fg.G).IsEqualTo((byte)2);
        await Assert.That(fg.B).IsEqualTo((byte)3);
    }

    [Test]
    public async Task BuildPlaceholder_NonPositiveDimensions_Throw()
    {
        await Assert.That(() => _kitty.BuildPlaceholder(1, 0, 1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => _kitty.BuildPlaceholder(1, 1, 0)).Throws<ArgumentOutOfRangeException>();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
