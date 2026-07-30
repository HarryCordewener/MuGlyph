namespace SharpMUTerm.Web.Tests;

/// <summary>
/// The web view's scheme gate. It is the only thing standing between "a link a world sent" and "a URL
/// this process opens", so it is the component that has to refuse <c>file://</c> — the TUI hands it
/// whatever the <c>mux:web:</c> payload carried and does not pre-filter, deliberately, because a second
/// gate somewhere else is a second thing to keep in step.
/// <para>
/// Every case here fails before a socket is opened, which is why they need no server: an unsupported
/// scheme returns a rendered error page rather than throwing, so the caller reports it like any other
/// failed page.
/// </para>
/// </summary>
public class WebPageFetcherSchemeTests
{
    /// <summary>
    /// Anything that is not <c>http</c>/<c>https</c> is refused, named, and never fetched. <c>file:</c> is
    /// the one that matters — a world could otherwise read a path off the machine into a pane — and the
    /// rest are here because "not http" is the rule, not a list of known-bad schemes.
    /// </summary>
    [Test]
    [Arguments("file:///etc/passwd")]
    [Arguments("file://localhost/etc/passwd")]
    [Arguments("ftp://example.org/secrets")]
    [Arguments("data:text/html,<b>hi</b>")]
    [Arguments("javascript:alert(1)")]
    [Arguments("mux:send:@shutdown")]
    [Arguments("not a url at all")]
    public async Task ANonHttpUrl_IsRefusedWithoutFetching(string url)
    {
        using var fetcher = new WebPageFetcher();

        var page = await fetcher.FetchAsync(url, 80);

        await Assert.That(string.Join("\n", page.Lines.Select(l => l.Text)))
            .Contains("Only http(s) URLs can be opened in the web view.");
    }
}
