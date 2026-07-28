using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Web;

/// <summary>A fetched-and-rendered web page: its title, source URL, styled lines, and inline images.</summary>
public sealed class WebPage
{
    public WebPage(string url, string? title, IReadOnlyList<StyledLine> lines, IReadOnlyList<WebImage>? images = null)
    {
        Url = url;
        Title = title;
        Lines = lines;
        Images = images ?? Array.Empty<WebImage>();
    }

    public string Url { get; }

    public string? Title { get; }

    public IReadOnlyList<StyledLine> Lines { get; }

    /// <summary>
    /// The page's <c>&lt;img&gt;</c> elements, each naming the line its placeholder occupies. Empty
    /// for non-HTML responses and error pages.
    /// </summary>
    public IReadOnlyList<WebImage> Images { get; }

    public static WebPage Error(string url, string message) =>
        new(url, "Error", new[] { StyledLine.FromText(message, TextStyle.Default.WithForeground(TerminalColor.FromIndex(9))) });
}
