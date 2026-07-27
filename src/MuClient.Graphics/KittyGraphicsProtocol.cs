using System.Text;
using MuClient.Core.Text;

namespace MuClient.Graphics;

/// <summary>Pixel format of a payload handed to the Kitty encoder.</summary>
public enum KittyImageFormat
{
    /// <summary>Raw 32-bit RGBA pixels (Kitty <c>f=32</c>).</summary>
    Rgba,

    /// <summary>A complete PNG file (Kitty <c>f=100</c>).</summary>
    Png,
}

/// <summary>
/// Deterministic encoder for the
/// <see href="https://sw.kovidgoyal.net/kitty/graphics-protocol/">Kitty graphics protocol</see>.
///
/// Every method returns the escape-sequence string(s); nothing is written to any console,
/// which keeps the encoder pure and golden-testable. The framing for one transmission is
/// <c>ESC _G &lt;controls&gt; ; &lt;base64-payload&gt; ESC \</c>. Large payloads are split into
/// chunks of at most 4096 base64 characters, each carrying <c>m=1</c> except the final
/// chunk which carries <c>m=0</c>.
/// </summary>
public sealed class KittyGraphicsProtocol
{
    /// <summary>The Unicode placeholder code point (<c>U+10EEEE</c>) that anchors an image to text cells.</summary>
    public const int PlaceholderCodePoint = 0x10EEEE;

    /// <summary>Maximum base64 characters per Kitty transmission chunk, per the spec.</summary>
    public const int MaxChunkBase64Length = 4096;

    private const string ApcStart = "\u001b_G"; // ESC _ G  (Application Programming Command)
    private const string St = "\u001b\\";       // ESC \    (String Terminator)

    /// <summary>
    /// The Kitty "row/column diacritics" table: combining marks whose position in this
    /// list encodes a 0-based row or column index. This is the canonical ordering from
    /// kitty's <c>rowcolumn-diacritics.json</c>; the first 64 entries are included, which
    /// comfortably covers the first 32 rows/columns required.
    /// </summary>
    public static readonly int[] RowColumnDiacritics =
    {
        0x0305, 0x030D, 0x030E, 0x0310, 0x0312, 0x033D, 0x033E, 0x033F,
        0x0346, 0x034A, 0x034B, 0x034C, 0x0350, 0x0351, 0x0352, 0x0357,
        0x035B, 0x0363, 0x0364, 0x0365, 0x0366, 0x0367, 0x0368, 0x0369,
        0x036A, 0x036B, 0x036C, 0x036D, 0x036E, 0x036F, 0x0483, 0x0484,
        0x0485, 0x0486, 0x0487, 0x0592, 0x0593, 0x0594, 0x0595, 0x0597,
        0x0598, 0x0599, 0x059C, 0x059D, 0x059E, 0x059F, 0x05A0, 0x05A1,
        0x05A8, 0x05A9, 0x05AB, 0x05AC, 0x05AF, 0x05C4, 0x0610, 0x0611,
        0x0612, 0x0613, 0x0614, 0x0615, 0x0616, 0x0617, 0x0657, 0x0658,
    };

    /// <summary>
    /// Transmits an image and displays it in one action (<c>a=T</c>). Returns the full
    /// escape sequence, split into <c>m=1</c>/<c>m=0</c> chunks when the base64 payload
    /// exceeds <see cref="MaxChunkBase64Length"/>.
    /// </summary>
    /// <param name="imageId">Client-assigned image id (<c>i=</c>).</param>
    /// <param name="payload">Raw RGBA pixels or a PNG file, per <paramref name="format"/>.</param>
    /// <param name="width">Source pixel width (<c>s=</c>).</param>
    /// <param name="height">Source pixel height (<c>v=</c>).</param>
    /// <param name="format">Payload format.</param>
    public string TransmitAndDisplay(
        int imageId,
        ReadOnlySpan<byte> payload,
        int width,
        int height,
        KittyImageFormat format)
    {
        if (imageId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageId), imageId, "Image id must be positive.");
        }

        var formatCode = format == KittyImageFormat.Png ? 100 : 32;
        var base64 = Convert.ToBase64String(payload);

        var builder = new StringBuilder();
        var chunkCount = Math.Max(1, (base64.Length + MaxChunkBase64Length - 1) / MaxChunkBase64Length);

        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            var start = chunkIndex * MaxChunkBase64Length;
            var length = Math.Min(MaxChunkBase64Length, base64.Length - start);
            var chunk = base64.Substring(start, length);
            var isLast = chunkIndex == chunkCount - 1;

            builder.Append(ApcStart);

            if (chunkIndex == 0)
            {
                // The first chunk carries the full control set.
                builder.Append("a=T,f=").Append(formatCode)
                    .Append(",i=").Append(imageId)
                    .Append(",s=").Append(width)
                    .Append(",v=").Append(height);
                builder.Append(",m=").Append(isLast ? '0' : '1');
            }
            else
            {
                // Continuation chunks only carry the more flag.
                builder.Append("m=").Append(isLast ? '0' : '1');
            }

            builder.Append(';').Append(chunk).Append(St);
        }

        return builder.ToString();
    }

    /// <summary>Deletes an image by id (<c>a=d,i=&lt;id&gt;</c>).</summary>
    public string Delete(int imageId)
    {
        if (imageId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageId), imageId, "Image id must be positive.");
        }

        return $"{ApcStart}a=d,i={imageId};{St}";
    }

    /// <summary>
    /// Builds the Unicode-placeholder grid for a previously transmitted image. Each cell
    /// is the placeholder rune <c>U+10EEEE</c> followed by two combining diacritics that
    /// encode its (row, column); the image id is carried in the foreground colour of every
    /// cell. Rendering this grid causes the terminal to composite the image over those
    /// real text cells. Returns one <see cref="StyledLine"/> per row.
    /// </summary>
    public IReadOnlyList<StyledLine> BuildPlaceholder(int imageId, int cols, int rows)
    {
        if (imageId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageId), imageId, "Image id must be positive.");
        }

        if (cols <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cols), cols, "Columns must be positive.");
        }

        if (rows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rows), rows, "Rows must be positive.");
        }

        if (rows > RowColumnDiacritics.Length || cols > RowColumnDiacritics.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rows),
                $"Placeholder grid up to {RowColumnDiacritics.Length}x{RowColumnDiacritics.Length} is supported.");
        }

        // The placeholder carries the image id in a 24-bit foreground colour, so ids above
        // 0xFFFFFF cannot round-trip. Reject them rather than silently truncating.
        if (imageId is < 0 or > 0xFFFFFF)
        {
            throw new ArgumentOutOfRangeException(
                nameof(imageId), imageId, "Placeholder image id must fit in 24 bits (0-0xFFFFFF).");
        }

        // Carry the 24-bit image id in the foreground colour, per the Kitty spec.
        var idColor = TerminalColor.FromRgb(
            (byte)((imageId >> 16) & 0xFF),
            (byte)((imageId >> 8) & 0xFF),
            (byte)(imageId & 0xFF));
        var style = TextStyle.Default.WithForeground(idColor);

        var lines = new StyledLine[rows];
        for (var row = 0; row < rows; row++)
        {
            var spans = new StyledSpan[cols];
            for (var col = 0; col < cols; col++)
            {
                var cell = new StringBuilder(4);
                cell.Append(char.ConvertFromUtf32(PlaceholderCodePoint));
                cell.Append(char.ConvertFromUtf32(RowColumnDiacritics[row]));
                cell.Append(char.ConvertFromUtf32(RowColumnDiacritics[col]));
                spans[col] = new StyledSpan(cell.ToString(), style);
            }

            lines[row] = new StyledLine(spans);
        }

        return lines;
    }
}
