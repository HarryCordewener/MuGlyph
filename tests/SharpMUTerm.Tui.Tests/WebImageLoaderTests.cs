using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The loader's gatekeeping and decode/downsample path. Every rejection here means the page keeps
/// its text placeholder, so these are the cases that decide whether a hostile or broken image can
/// disturb the client — and they are all reachable without a network.
/// </summary>
public class WebImageLoaderTests
{
    /// <summary>A real PNG of the given size, so the decode path runs on genuine image bytes.</summary>
    private static byte[] SamplePng(int width = 32, int height = 32)
    {
        using var image = new Image<Rgb24>(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image[x, y] = new Rgb24((byte)(x % 256), (byte)(y % 256), 128);
            }
        }

        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    // ---- Media types ---------------------------------------------------------------------------

    [Test]
    [Arguments("image/png")]
    [Arguments("image/jpeg")]
    [Arguments("image/gif")]
    [Arguments("image/webp")]
    [Arguments("IMAGE/PNG")]
    public async Task RasterImageTypes_AreAccepted(string mediaType)
    {
        await Assert.That(WebImageLoader.IsDecodableImageType(mediaType)).IsTrue();
    }

    [Test]
    [Arguments("text/html")]
    [Arguments("application/json")]
    [Arguments("image/svg+xml")]
    [Arguments("")]
    public async Task NonDecodableTypes_AreRejected(string mediaType)
    {
        // SVG is an image/* type the decoder cannot rasterise, so accepting it would only ever
        // produce a failed decode.
        await Assert.That(WebImageLoader.IsDecodableImageType(mediaType)).IsFalse();
    }

    // ---- data: URIs ----------------------------------------------------------------------------

    [Test]
    public async Task Base64DataUri_IsDecoded()
    {
        var payload = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
        var bytes = WebImageLoader.ParseDataUri($"data:image/png;base64,{payload}");
        await Assert.That(bytes).IsEquivalentTo(new byte[] { 1, 2, 3, 4 });
    }

    [Test]
    [Arguments("data:image/png,notbase64")]
    [Arguments("data:image/png;base64,!!!not-base64!!!")]
    [Arguments("nocomma")]
    [Arguments("data:")]
    public async Task MalformedOrNonBase64DataUris_YieldNull(string uri)
    {
        await Assert.That(WebImageLoader.ParseDataUri(uri)).IsNull();
    }

    [Test]
    public async Task OversizedDataUri_IsRejectedBeforeDecoding()
    {
        // Guards against a page inflating memory through a giant inline payload.
        var huge = "data:image/png;base64," + new string('A', (int)((WebImageLoader.MaxImageBytes / 3) * 4) + 8);
        await Assert.That(WebImageLoader.ParseDataUri(huge)).IsNull();
    }

    // ---- Decoding + downsampling ---------------------------------------------------------------

    [Test]
    public async Task ValidPng_DecodesToItsNaturalSizeWhenItAlreadyFits()
    {
        var buffer = WebImageLoader.Decode(SamplePng(32, 32), availableColumns: 200);
        await Assert.That(buffer).IsNotNull();
        await Assert.That(buffer!.Width).IsEqualTo(32);
        await Assert.That(buffer.Height).IsEqualTo(32);
    }

    [Test]
    public async Task LargeImage_IsDownsampledToTheCellBoxItWillOccupy()
    {
        // 400x200 fits into 80 cols x 20 rows at the default row ceiling, so the buffer handed to
        // the control is exactly that: 80 pixels wide, 40 tall (two pixel rows per cell).
        var buffer = WebImageLoader.Decode(SamplePng(400, 200), availableColumns: 100);
        await Assert.That(buffer).IsNotNull();

        var expected = WebImageLayout.Fit(400, 200, availableColumns: 100);
        await Assert.That(buffer!.Width).IsEqualTo(expected.Columns);
        await Assert.That(buffer.Height).IsEqualTo(expected.Rows * WebImageLayout.PixelsPerCell);
    }

    [Test]
    public async Task DecodedBufferIsNeverWiderThanTheColumnBudget()
    {
        foreach (var columns in new[] { 10, 40, 100 })
        {
            var buffer = WebImageLoader.Decode(SamplePng(400, 200), columns);
            await Assert.That(buffer!.Width).IsLessThanOrEqualTo(columns);
        }
    }

    [Test]
    public async Task DecodedBufferNeverExceedsTheRowCeiling()
    {
        var buffer = WebImageLoader.Decode(SamplePng(64, 2000), availableColumns: 120);
        await Assert.That(buffer).IsNotNull();
        await Assert.That(buffer!.Height / WebImageLayout.PixelsPerCell)
            .IsLessThanOrEqualTo(WebImageLayout.MaxRows);
    }

    [Test]
    public async Task TinyImage_IsRejectedRatherThanDrawn()
    {
        await Assert.That(WebImageLoader.Decode(SamplePng(8, 8), availableColumns: 200)).IsNull();
    }

    [Test]
    [Arguments(new byte[0])]
    [Arguments(new byte[] { 0x00, 0x01, 0x02, 0x03 })]
    public async Task GarbageBytes_DecodeToNullRatherThanThrowing(byte[] bytes)
    {
        await Assert.That(WebImageLoader.Decode(bytes, availableColumns: 80)).IsNull();
    }

    [Test]
    public async Task HtmlErrorPageBytes_DecodeToNull()
    {
        // A server that answers an image request with an HTML error page must not crash the view.
        var html = System.Text.Encoding.UTF8.GetBytes("<html><body>404</body></html>");
        await Assert.That(WebImageLoader.Decode(html, availableColumns: 80)).IsNull();
    }

    [Test]
    public async Task NoColumnsAvailable_DecodesToNull()
    {
        await Assert.That(WebImageLoader.Decode(SamplePng(), availableColumns: 0)).IsNull();
    }

    [Test]
    public async Task Decode_RejectsNullBytes()
    {
        await Assert.That(() => WebImageLoader.Decode(null!, 80)).Throws<ArgumentNullException>();
    }

    // ---- Fetch gatekeeping (no network needed) --------------------------------------------------

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("ftp://example.com/pic.png")]
    [Arguments("file:///etc/passwd")]
    [Arguments("not a url")]
    [Arguments("javascript:alert(1)")]
    public async Task NonHttpSources_AreNeverFetched(string url)
    {
        using var loader = new WebImageLoader();
        // Rejected on the scheme, before any request is made — so this cannot hit the network.
        await Assert.That(await loader.LoadAsync(url, 80)).IsNull();
    }
}
