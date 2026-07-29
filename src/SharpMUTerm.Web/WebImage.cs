using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Web;

/// <summary>
/// An <c>&lt;img&gt;</c> the renderer met, and the line its text placeholder occupies in the rendered
/// page. A view with inline graphics replaces exactly that line with the decoded picture; a view
/// without them leaves the placeholder where it is, which is why the placeholder is always emitted.
/// </summary>
/// <param name="LineIndex">Index into <see cref="WebPage.Lines"/> of the placeholder's first line.</param>
/// <param name="Source">The image URL, already resolved against the page's base URL.</param>
/// <param name="Alt">The <c>alt</c> text, or null when the element carried none.</param>
/// <param name="PlaceholderText">The text shown in place of the image (e.g. <c>[image: a cat]</c>).</param>
/// <param name="LineCount">
/// How many lines the placeholder occupies. Normally one, but a long <c>alt</c> wraps like any other
/// text, and a view that swaps in the picture has to consume <em>all</em> of them — replacing only the
/// first leaves the tail of the alt text stranded under the image.
/// </param>
public sealed record WebImage(int LineIndex, string Source, string? Alt, string PlaceholderText, int LineCount = 1);

/// <summary>
/// The full result of rendering an HTML document: the styled lines plus the images found in them.
/// </summary>
/// <param name="Lines">The page as styled text lines.</param>
/// <param name="Images">Every <c>&lt;img&gt;</c> with a usable <c>src</c>, in document order.</param>
public sealed record HtmlRenderResult(IReadOnlyList<StyledLine> Lines, IReadOnlyList<WebImage> Images);
