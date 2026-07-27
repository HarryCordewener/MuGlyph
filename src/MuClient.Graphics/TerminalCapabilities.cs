namespace MuClient.Graphics;

/// <summary>
/// An immutable snapshot of what the host terminal can display. Produced by
/// <see cref="CapabilityProbe"/>. Never assumes more than the environment advertises —
/// a headless sandbox degrades cleanly to <see cref="GraphicsProtocol.None"/>/<see cref="GraphicsProtocol.HalfBlock"/>.
/// </summary>
public sealed class TerminalCapabilities
{
    public TerminalCapabilities(
        GraphicsProtocol protocol,
        bool supportsTrueColor,
        bool supportsKittyGraphics,
        bool supportsSixel)
    {
        Protocol = protocol;
        SupportsTrueColor = supportsTrueColor;
        SupportsKittyGraphics = supportsKittyGraphics;
        SupportsSixel = supportsSixel;
    }

    /// <summary>The best inline-graphics protocol available.</summary>
    public GraphicsProtocol Protocol { get; }

    /// <summary>True when the terminal advertises 24-bit colour (needed for good half-block output).</summary>
    public bool SupportsTrueColor { get; }

    /// <summary>True when the Kitty graphics protocol is available.</summary>
    public bool SupportsKittyGraphics { get; }

    /// <summary>True when Sixel raster graphics are available.</summary>
    public bool SupportsSixel { get; }

    public override string ToString() =>
        $"{Protocol} (truecolor={SupportsTrueColor}, kitty={SupportsKittyGraphics}, sixel={SupportsSixel})";
}
