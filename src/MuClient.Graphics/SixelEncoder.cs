using System.Text;

namespace MuClient.Graphics;

/// <summary>
/// Encodes an <see cref="IImageSource"/> to a DEC Sixel data string
/// (<c>ESC P q … ESC \</c>). Colours are quantised to a 6-level-per-channel RGB cube
/// (≤216 registers, within Sixel's 256-register budget); each register is declared with
/// <c>#n;2;r;g;b</c> (channels scaled 0-100), then pixels are emitted in six-row bands.
///
/// The implementation favours correctness and determinism (so it can be golden-tested)
/// over compactness — it performs no run-length compression.
/// </summary>
public sealed class SixelEncoder
{
    private const string Intro = "\u001bPq";      // ESC P q : start Sixel, default params.
    private const string Terminator = "\u001b\\"; // ESC \   : String Terminator.

    /// <summary>Encodes the image to a complete Sixel data string.</summary>
    public string Encode(IImageSource image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var width = image.Width;
        var height = image.Height;

        // Quantise every pixel to a palette index, assigning registers on first appearance.
        var palette = new List<(int R, int G, int B)>();
        var paletteLookup = new Dictionary<int, int>();
        var indices = new int[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = image.GetPixel(x, y);
                var quant = Quantize(pixel);
                var key = (quant.R << 16) | (quant.G << 8) | quant.B;
                if (!paletteLookup.TryGetValue(key, out var index))
                {
                    index = palette.Count;
                    paletteLookup[key] = index;
                    palette.Add(quant);
                }

                indices[(y * width) + x] = index;
            }
        }

        var sb = new StringBuilder();
        sb.Append(Intro);

        // Register declarations, in assignment order.
        for (var i = 0; i < palette.Count; i++)
        {
            var (r, g, b) = palette[i];
            sb.Append('#').Append(i).Append(";2;").Append(r).Append(';').Append(g).Append(';').Append(b);
        }

        var bandCount = (height + 5) / 6;
        for (var band = 0; band < bandCount; band++)
        {
            var bandTop = band * 6;
            var bandRows = Math.Min(6, height - bandTop);

            // Which registers actually appear in this band (in assignment order).
            var colorsInBand = new SortedSet<int>();
            for (var row = 0; row < bandRows; row++)
            {
                var y = bandTop + row;
                for (var x = 0; x < width; x++)
                {
                    colorsInBand.Add(indices[(y * width) + x]);
                }
            }

            var first = true;
            foreach (var color in colorsInBand)
            {
                if (!first)
                {
                    // Carriage return: overlay the next colour on the same band.
                    sb.Append('$');
                }

                first = false;

                sb.Append('#').Append(color);
                for (var x = 0; x < width; x++)
                {
                    var mask = 0;
                    for (var row = 0; row < bandRows; row++)
                    {
                        var y = bandTop + row;
                        if (indices[(y * width) + x] == color)
                        {
                            mask |= 1 << row;
                        }
                    }

                    sb.Append((char)(63 + mask));
                }
            }

            // Graphics newline between bands (not after the final band).
            if (band < bandCount - 1)
            {
                sb.Append('-');
            }
        }

        sb.Append(Terminator);
        return sb.ToString();
    }

    /// <summary>
    /// Quantises an RGBA pixel to the 6-level RGB cube and returns each channel scaled to
    /// the Sixel 0-100 range (levels 0..5 → 0,20,40,60,80,100).
    /// </summary>
    private static (int R, int G, int B) Quantize(Rgba32 pixel)
    {
        static int Channel(byte value)
        {
            var level = value * 6 / 256; // 0..5
            return level * 20;           // 0,20,40,60,80,100
        }

        return (Channel(pixel.R), Channel(pixel.G), Channel(pixel.B));
    }
}
