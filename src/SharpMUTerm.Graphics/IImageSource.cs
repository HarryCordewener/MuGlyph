namespace SharpMUTerm.Graphics;

/// <summary>
/// A 32-bit non-premultiplied RGBA pixel. Keeps the graphics subsystem free of any
/// image-decoding dependency: callers decode into this and hand us a pixel source.
/// </summary>
public readonly struct Rgba32 : IEquatable<Rgba32>
{
    public Rgba32(byte r, byte g, byte b, byte a = 255)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    public byte R { get; }

    public byte G { get; }

    public byte B { get; }

    public byte A { get; }

    public bool Equals(Rgba32 other) => R == other.R && G == other.G && B == other.B && A == other.A;

    public override bool Equals(object? obj) => obj is Rgba32 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(R, G, B, A);

    public static bool operator ==(Rgba32 left, Rgba32 right) => left.Equals(right);

    public static bool operator !=(Rgba32 left, Rgba32 right) => !left.Equals(right);

    public override string ToString() => $"rgba({R},{G},{B},{A})";
}

/// <summary>
/// An abstract source of RGBA pixels. Implementations may be backed by a decoded
/// bitmap, a procedural generator, or a test fixture. UI-agnostic and allocation-free
/// to read.
/// </summary>
public interface IImageSource
{
    /// <summary>Image width in pixels (&gt; 0).</summary>
    int Width { get; }

    /// <summary>Image height in pixels (&gt; 0).</summary>
    int Height { get; }

    /// <summary>Returns the pixel at (<paramref name="x"/>, <paramref name="y"/>), origin top-left.</summary>
    Rgba32 GetPixel(int x, int y);
}

/// <summary>
/// A simple in-memory <see cref="IImageSource"/> backed by a row-major
/// <see cref="Rgba32"/> array. Used by callers that already have raw pixels and by tests.
/// </summary>
public sealed class MemoryImageSource : IImageSource
{
    private readonly Rgba32[] _pixels;

    public MemoryImageSource(int width, int height, Rgba32[] pixels)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");
        }

        ArgumentNullException.ThrowIfNull(pixels);

        if (pixels.Length != width * height)
        {
            throw new ArgumentException(
                $"Pixel array length {pixels.Length} does not match {width}x{height} = {width * height}.",
                nameof(pixels));
        }

        Width = width;
        Height = height;
        _pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public Rgba32 GetPixel(int x, int y)
    {
        if ((uint)x >= (uint)Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x), x, "X is outside the image bounds.");
        }

        if ((uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y), y, "Y is outside the image bounds.");
        }

        return _pixels[(y * Width) + x];
    }
}
