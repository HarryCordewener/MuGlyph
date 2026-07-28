using MuClient.Core.Text;

namespace MuClient.Web;

/// <summary>A fetched-and-rendered web page: its title, source URL, and styled lines.</summary>
public sealed class WebPage
{
    public WebPage(string url, string? title, IReadOnlyList<StyledLine> lines)
    {
        Url = url;
        Title = title;
        Lines = lines;
    }

    public string Url { get; }

    public string? Title { get; }

    public IReadOnlyList<StyledLine> Lines { get; }

    public static WebPage Error(string url, string message) =>
        new(url, "Error", new[] { StyledLine.FromText(message, TextStyle.Default.WithForeground(TerminalColor.FromIndex(9))) });
}
