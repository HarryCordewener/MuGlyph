using System.Net;
using SharpConsoleUI.Imaging;

namespace SharpMUTerm.Tui;

/// <summary>
/// Fetches an inline web image and decodes it into a framework <see cref="PixelBuffer"/>, resized to
/// the cell box it will occupy. Deliberately conservative in the same spirit as
/// <c>WebPageFetcher</c>: http(s) and <c>data:</c> only, an image content type, a byte cap, and a
/// timeout, so a hostile page cannot hang the client or exhaust it.
///
/// <para>The resize happens here rather than at paint time on purpose. The framework's
/// <c>ImageControl</c> measures to its source's natural size when the layout constraint is unbounded
/// (as it is inside a scrollable panel), so handing it a 1200-pixel-wide photo would have it claim
/// 1200 columns. Downsampling first makes the control's natural size the size we actually want, and
/// caps what the Kitty encoder has to PNG on every source change.</para>
///
/// <para>Every failure path returns <c>null</c>. A null simply means the placeholder line stays —
/// there is no error state to render, because a text-mode browser showing <c>[image: a cat]</c> is a
/// perfectly good outcome.</para>
/// </summary>
internal sealed class WebImageLoader : IDisposable
{
    /// <summary>Refuse anything larger than this; big images are slow to decode and worse to look at.</summary>
    public const long MaxImageBytes = 8 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public WebImageLoader(HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _http = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };

        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd(
                "SharpMUTerm/0.1 (+https://github.com/SharpMUSH/SharpMUTerm)"))
        {
            // A restrictive HttpClient may reject the header; not fatal.
        }
    }

    /// <summary>
    /// Fetches, decodes, and downsamples an image to fit <paramref name="availableColumns"/>.
    /// Returns null when the URL is unusable, the fetch fails, the payload is not a decodable
    /// image, or the result would be too small to be worth drawing.
    /// </summary>
    public async Task<PixelBuffer?> LoadAsync(
        string url, int availableColumns, CancellationToken cancellationToken = default)
    {
        var bytes = await FetchAsync(url, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        return Decode(bytes, availableColumns);
    }

    /// <summary>
    /// Decodes image bytes and downsamples to the target box. Split out from the fetch so the sizing
    /// half is testable without a network.
    /// </summary>
    public static PixelBuffer? Decode(byte[] imageBytes, int availableColumns)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0 || availableColumns <= 0)
        {
            return null;
        }

        PixelBuffer decoded;
        try
        {
            using var stream = new MemoryStream(imageBytes, writable: false);
            decoded = PixelBuffer.FromStream(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Corrupt payload, unsupported format, or past the framework's dimension ceiling.
            return null;
        }

        if (!WebImageLayout.IsWorthRendering(decoded.Width, decoded.Height))
        {
            return null;
        }

        var box = WebImageLayout.Fit(decoded.Width, decoded.Height, availableColumns);
        if (box.Columns <= 0 || box.Rows <= 0)
        {
            return null;
        }

        var targetPixelHeight = box.Rows * WebImageLayout.PixelsPerCell;
        if (box.Columns == decoded.Width && targetPixelHeight == decoded.Height)
        {
            return decoded;
        }

        try
        {
            return decoded.Resize(box.Columns, targetPixelHeight);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private async Task<byte[]?> FetchAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return ParseDataUri(url);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        try
        {
            using var response = await _http
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!IsDecodableImageType(mediaType))
            {
                return null;
            }

            if (response.Content.Headers.ContentLength > MaxImageBytes)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return bytes.Length > MaxImageBytes ? null : bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// True for raster image media types the decoder can actually handle. SVG is excluded: it is an
    /// <c>image/*</c> type that ImageSharp cannot rasterise, so accepting it would only ever produce
    /// a failed decode.
    /// </summary>
    public static bool IsDecodableImageType(string mediaType) =>
        mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
        !mediaType.Contains("svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>Decodes a base64 <c>data:</c> URI. Non-base64 and malformed URIs yield null.</summary>
    public static byte[]? ParseDataUri(string dataUri)
    {
        ArgumentNullException.ThrowIfNull(dataUri);

        var comma = dataUri.IndexOf(',');
        if (comma < 0)
        {
            return null;
        }

        var header = dataUri[..comma];
        var payload = dataUri[(comma + 1)..];

        if (!header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Base64 expands 3 bytes into 4 characters, so cap the encoded form proportionally.
        if (payload.Length > (MaxImageBytes / 3) * 4)
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
