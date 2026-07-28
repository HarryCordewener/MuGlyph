using System.Net;

namespace MuClient.Web;

/// <summary>
/// Fetches an <c>http(s)</c> URL and renders it to a <see cref="WebPage"/> via
/// <see cref="HtmlStyledRenderer"/>. Non-HTML responses render as plain text. Deliberately
/// conservative: only http/https, a byte cap, and a timeout, so a hostile page can't hang or
/// exhaust the client.
/// </summary>
public sealed class WebPageFetcher : IDisposable
{
    private readonly HttpClient _http;
    private readonly long _maxBytes;

    public WebPageFetcher(HttpClient? httpClient = null, long maxBytes = 4 * 1024 * 1024)
    {
        _http = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd("MuGlyph/0.1 (+https://github.com/HarryCordewener/MuGlyph)"))
        {
            // A restrictive HttpClient may reject the header; not fatal.
        }

        _maxBytes = maxBytes;
    }

    public async Task<WebPage> FetchAsync(string url, int width = 80, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return WebPage.Error(url, "Only http(s) URLs can be opened in the web view.");
        }

        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var body = await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase) || contentType.Length == 0)
            {
                var renderer = new HtmlStyledRenderer(uri.ToString());
                var lines = renderer.Render(body, width);
                return new WebPage(uri.ToString(), HtmlStyledRenderer.GetTitle(body), lines);
            }

            // Non-HTML: render as plain text lines.
            var parser = new Core.Text.AnsiParser();
            var textLines = parser.Feed(body.Replace("\r\n", "\n")).ToList();
            var tail = parser.Flush();
            if (tail is not null)
            {
                textLines.Add(tail);
            }

            return new WebPage(uri.ToString(), uri.Host, textLines);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return WebPage.Error(url, $"Could not load {url}: {ex.Message}");
        }
    }

    private async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var limited = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (limited.Length + read > _maxBytes)
            {
                limited.Write(buffer, 0, (int)(_maxBytes - limited.Length));
                break;
            }

            limited.Write(buffer, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(limited.ToArray());
    }

    public void Dispose() => _http.Dispose();
}
