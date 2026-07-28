using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Graphics;

/// <summary>How an <see cref="InlineImageOutput"/> should be consumed by the renderer.</summary>
public enum InlineImageKind
{
    /// <summary>No graphics available: show <see cref="InlineImageOutput.PlaceholderText"/> as plain text.</summary>
    Text,

    /// <summary>Styled half-block cells in <see cref="InlineImageOutput.Lines"/>.</summary>
    HalfBlock,

    /// <summary>A raw escape-sequence string in <see cref="InlineImageOutput.EscapeSequence"/> (Sixel).</summary>
    Sixel,

    /// <summary>A raw escape-sequence string in <see cref="InlineImageOutput.EscapeSequence"/> (Kitty).</summary>
    Kitty,
}

/// <summary>
/// A discriminated result of rendering an inline image: exactly one payload is populated
/// according to <see cref="Kind"/>. Escape-sequence kinds carry a string; half-block carries
/// styled lines; the text kind carries a plain placeholder.
/// </summary>
public sealed class InlineImageOutput
{
    private InlineImageOutput(
        InlineImageKind kind,
        string? escapeSequence,
        IReadOnlyList<StyledLine>? lines,
        string? placeholderText)
    {
        Kind = kind;
        EscapeSequence = escapeSequence;
        Lines = lines;
        PlaceholderText = placeholderText;
    }

    public InlineImageKind Kind { get; }

    /// <summary>The escape sequence for <see cref="InlineImageKind.Kitty"/>/<see cref="InlineImageKind.Sixel"/>.</summary>
    public string? EscapeSequence { get; }

    /// <summary>The styled rows for <see cref="InlineImageKind.HalfBlock"/>.</summary>
    public IReadOnlyList<StyledLine>? Lines { get; }

    /// <summary>The plain placeholder for <see cref="InlineImageKind.Text"/>.</summary>
    public string? PlaceholderText { get; }

    public static InlineImageOutput ForKitty(string escapeSequence) =>
        new(InlineImageKind.Kitty, escapeSequence, null, null);

    public static InlineImageOutput ForSixel(string escapeSequence) =>
        new(InlineImageKind.Sixel, escapeSequence, null, null);

    public static InlineImageOutput ForHalfBlock(IReadOnlyList<StyledLine> lines) =>
        new(InlineImageKind.HalfBlock, null, lines, null);

    public static InlineImageOutput ForText(string placeholderText) =>
        new(InlineImageKind.Text, null, null, placeholderText);
}

/// <summary>
/// Picks the best inline-image encoding for a given <see cref="TerminalCapabilities"/> and
/// renders an <see cref="IImageSource"/> accordingly. This is the glue callers use; the
/// individual encoders remain independently usable.
/// </summary>
public sealed class InlineImageRenderer
{
    private readonly KittyGraphicsProtocol _kitty;
    private readonly SixelEncoder _sixel;
    private readonly HalfBlockRenderer _halfBlock;

    public InlineImageRenderer()
        : this(new KittyGraphicsProtocol(), new SixelEncoder(), new HalfBlockRenderer())
    {
    }

    public InlineImageRenderer(
        KittyGraphicsProtocol kitty,
        SixelEncoder sixel,
        HalfBlockRenderer halfBlock)
    {
        _kitty = kitty ?? throw new ArgumentNullException(nameof(kitty));
        _sixel = sixel ?? throw new ArgumentNullException(nameof(sixel));
        _halfBlock = halfBlock ?? throw new ArgumentNullException(nameof(halfBlock));
    }

    /// <summary>
    /// Renders <paramref name="image"/> using the highest protocol in <paramref name="capabilities"/>.
    /// The half-block fallback is bounded by <paramref name="maxCols"/>/<paramref name="maxRows"/>;
    /// <paramref name="imageId"/> is used only for the Kitty transmission.
    /// </summary>
    public InlineImageOutput Render(
        IImageSource image,
        TerminalCapabilities capabilities,
        int imageId = 1,
        int maxCols = 80,
        int maxRows = 24)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(capabilities);

        switch (capabilities.Protocol)
        {
            case GraphicsProtocol.Kitty:
                var rgba = ToRgbaBytes(image);
                var sequence = _kitty.TransmitAndDisplay(
                    imageId, rgba, image.Width, image.Height, KittyImageFormat.Rgba);
                return InlineImageOutput.ForKitty(sequence);

            case GraphicsProtocol.Sixel:
                return InlineImageOutput.ForSixel(_sixel.Encode(image));

            case GraphicsProtocol.HalfBlock:
                return InlineImageOutput.ForHalfBlock(_halfBlock.Render(image, maxCols, maxRows));

            case GraphicsProtocol.None:
            default:
                return InlineImageOutput.ForText($"[image {image.Width}x{image.Height}]");
        }
    }

    /// <summary>Flattens an image to a row-major RGBA byte buffer for Kitty transmission.</summary>
    public static byte[] ToRgbaBytes(IImageSource image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var bytes = new byte[image.Width * image.Height * 4];
        var offset = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var p = image.GetPixel(x, y);
                bytes[offset++] = p.R;
                bytes[offset++] = p.G;
                bytes[offset++] = p.B;
                bytes[offset++] = p.A;
            }
        }

        return bytes;
    }
}
