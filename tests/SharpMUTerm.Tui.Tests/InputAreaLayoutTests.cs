using System.Text.RegularExpressions;
using SharpConsoleUI.Drivers;
using SharpMUTerm.Graphics;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// What the window actually gives the input area, in rows.
/// <para>
/// <see cref="InputLayoutTests"/> covers the arithmetic — how tall a bar wants to be for a given text
/// at a given width — and every one of those assertions passes whatever the window then does with the
/// answer. That is the gap these close: a bar's height is a <em>request</em>, made in
/// <see cref="InputBarControl.MeasureDOM"/>, and SharpConsoleUI grants it in
/// <c>WindowContentLayout</c>, which reserves the sticky bands before the workspace is measured at all
/// and applies no minimum of its own. A bar that asks for three rows and is arranged at none is a
/// disagreement no arithmetic test can see, so these read the granted number back off the arranged
/// controls — and then check the frame carries that many rows of each bar's band, because "the second
/// bar's tone appears nowhere" is the form the defect gets reported in.
/// </para>
/// </summary>
/// <remarks>
/// Serialised for the same reason <see cref="PaneDragEndToEndTests"/> is:
/// <see cref="SharpMUTermApp.RenderSnapshot"/> redirects the process-global <c>Console.Out</c> to
/// capture the frame, and <see cref="Frame"/> redirects <c>Console.In</c>.
/// </remarks>
[NotInParallel]
public class InputAreaLayoutTests
{
    private const int DefaultRows = 3; // InputSettings.Rows — one bar's height before anything wraps
    private const int MaxRows = 8;     // InputSettings.MaxRows — the most it grows to as it wraps

    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    private sealed record Rendered(
        string Ansi,
        (int Header, int Workspace, int Primary, int Second, int Status) Rows,
        string ArmedBand,
        string IdleBand);

    /// <summary>Renders one demo frame at a given terminal size and hands back the frame and its layout.</summary>
    private static Rendered Frame(int width, int height, string? view = null)
    {
        // The window system reads the console for input even headless; a null reader returns EOF.
        Console.SetIn(TextReader.Null);

        var app = new SharpMUTermApp(DemoScene.Build(), Headless, new HeadlessConsoleDriver(width, height));
        var ansi = app.RenderSnapshot(view);
        var (armed, idle) = app.InputBandColors;
        return new Rendered(ansi, app.LaidOutRows, Sgr(armed), Sgr(idle));
    }

    /// <summary>The truecolor background escape a colour is written as, e.g. <c>48;2;51;57;76</c>.</summary>
    private static string Sgr(SharpConsoleUI.Color color) => $"48;2;{color.R};{color.G};{color.B}";

    /// <summary>
    /// How many rows of the frame are painted in <paramref name="background"/> from edge to edge. The
    /// frame is a stream of cursor moves and SGR runs, so the rows are walked the way a terminal walks
    /// them: the last <c>48;2;r;g;b</c> seen is the colour of every cell written until the next one.
    /// A bar arranged at zero rows paints nothing and counts zero here — which is exactly the reading
    /// that would have caught this, and exactly what no arithmetic test can produce.
    /// </summary>
    private static int RowsPainted(string ansi, int width, int height, string background)
    {
        var cells = new string?[height, width];
        var current = (string?)null;
        var (row, column) = (0, 0);

        foreach (Match token in Regex.Matches(ansi, @"\x1b\[([0-9;]*)([A-Za-z])|([^\x1b\r\n])"))
        {
            if (token.Groups[3].Success)
            {
                if (row < height && column < width)
                {
                    cells[row, column] = current;
                }

                column++;
                continue;
            }

            var parameters = token.Groups[1].Value;
            switch (token.Groups[2].Value)
            {
                case "H":
                    var at = parameters.Split(';');
                    row = at[0].Length > 0 ? int.Parse(at[0]) - 1 : 0;
                    column = at.Length > 1 && at[1].Length > 0 ? int.Parse(at[1]) - 1 : 0;
                    break;
                case "m":
                    // A reset or a new background ends the run; anything else (bold, foreground) does not.
                    if (parameters.Length == 0 || parameters == "0" || parameters.Contains("49"))
                    {
                        current = null;
                    }

                    if (parameters.Contains("48;2;"))
                    {
                        current = parameters[parameters.IndexOf("48;2;", StringComparison.Ordinal)..];
                    }

                    break;
            }
        }

        var painted = 0;
        for (var y = 0; y < height; y++)
        {
            var full = true;
            for (var x = 0; x < width && full; x++)
            {
                full = cells[y, x]?.StartsWith(background, StringComparison.Ordinal) == true;
            }

            painted += full ? 1 : 0;
        }

        return painted;
    }

    /// <summary>
    /// One bar at its configured height, with the workspace taking what is left and nothing over- or
    /// under-committed: the five bands add up to the terminal exactly.
    /// </summary>
    [Test]
    [Arguments(120, 34)]
    [Arguments(100, 24)]
    public async Task OneBar_GetsItsConfiguredRows_AndTheWorkspaceTakesTheRest(int width, int height)
    {
        var frame = Frame(width, height);

        await Assert.That(frame.Rows.Primary).IsEqualTo(DefaultRows);
        await Assert.That(frame.Rows.Second).IsEqualTo(0);
        await Assert.That(frame.Rows.Workspace)
            .IsEqualTo(height - frame.Rows.Header - DefaultRows - frame.Rows.Status);
    }

    /// <summary>
    /// Both bars at their configured height, and the workspace — not the input area — is what shrinks
    /// to fit them. The second bar is raised through ⌃B i rather than set up beforehand, because the
    /// window has already laid itself out once by then and has to give the rows back.
    /// </summary>
    [Test]
    [Arguments(120, 34)]
    [Arguments(100, 24)]
    public async Task TwoBars_EachGetTheirRows_AndTheWorkspaceGivesThemUp(int width, int height)
    {
        Console.SetIn(TextReader.Null);
        var app = new SharpMUTermApp(DemoScene.Build(), Headless, new HeadlessConsoleDriver(width, height));

        app.RenderSnapshot();
        var one = app.LaidOutRows;

        app.SimulateKey(new ConsoleKeyInfo('\0', ConsoleKey.B, false, false, true));
        app.SimulateKey(new ConsoleKeyInfo('i', ConsoleKey.I, false, false, false));
        app.RenderSnapshot();
        var two = app.LaidOutRows;

        await Assert.That(two.Primary).IsEqualTo(DefaultRows);
        await Assert.That(two.Second).IsEqualTo(DefaultRows);
        await Assert.That(two.Workspace).IsEqualTo(one.Workspace - DefaultRows);
        await Assert.That(two.Header + two.Workspace + two.Primary + two.Second + two.Status)
            .IsEqualTo(height);
    }

    /// <summary>
    /// The frame carries as many rows of each band as the layout granted. This is the assertion that
    /// speaks the same language as the bug report: a bar squeezed to nothing is invisible to a glance
    /// at the frame and silent in every unit test, but its colour simply stops being on the screen.
    /// </summary>
    [Test]
    [Arguments(120, 34)]
    [Arguments(100, 24)]
    public async Task BothBands_ArePaintedForAsManyRowsAsTheyWereGiven(int width, int height)
    {
        // draft2 raises the second bar, puts a line in each, and arms the second — so the armed band
        // belongs to the second bar and the idle one to the primary.
        var frame = Frame(width, height, "draft2");

        await Assert.That(RowsPainted(frame.Ansi, width, height, frame.IdleBand))
            .IsEqualTo(frame.Rows.Primary);
        await Assert.That(RowsPainted(frame.Ansi, width, height, frame.ArmedBand))
            .IsEqualTo(frame.Rows.Second);
        await Assert.That(frame.Rows.Primary).IsEqualTo(DefaultRows);
        await Assert.That(frame.Rows.Second).IsEqualTo(DefaultRows);
    }

    /// <summary>
    /// A draft too long for three rows grows the bar, and the rows come out of the workspace rather
    /// than out of the bar's own request. Narrow enough that the demo's draft has to wrap past its
    /// floor, which is what makes the bar ask for more than it started with.
    /// </summary>
    [Test]
    public async Task AWrappedDraft_GrowsTheBarOutOfTheWorkspace()
    {
        var plain = Frame(60, 30);
        var grown = Frame(60, 30, "draft");

        await Assert.That(grown.Rows.Primary).IsGreaterThan(DefaultRows);
        await Assert.That(grown.Rows.Primary).IsLessThanOrEqualTo(MaxRows);
        await Assert.That(grown.Rows.Workspace)
            .IsEqualTo(plain.Rows.Workspace - (grown.Rows.Primary - plain.Rows.Primary));
    }

    /// <summary>
    /// The veto: a terminal small enough that the configured heights would swallow it keeps output
    /// anyway. The bars are capped against the rows the header and the status line leave — counting
    /// that chrome is the whole point, since a narrow terminal wraps both of them onto a second row,
    /// and a veto that assumed one row each handed all six rows of an 80×6 window to the input area.
    /// </summary>
    [Test]
    [Arguments(80, 6)]
    [Arguments(80, 8)]
    [Arguments(60, 10)]
    [Arguments(100, 12)]
    public async Task ASmallTerminal_StillHasOutputAboveTheInputArea(int width, int height)
    {
        var frame = Frame(width, height);

        await Assert.That(frame.Rows.Primary).IsGreaterThanOrEqualTo(1);
        await Assert.That(frame.Rows.Workspace).IsGreaterThanOrEqualTo(1);
        await Assert.That(frame.Rows.Primary).IsLessThanOrEqualTo(frame.Rows.Workspace);
    }

    /// <summary>
    /// The <em>first</em> frame is laid out for the terminal it is on. The header's status cluster is
    /// right-aligned by padding it out to the window's width, and the header markup is built in the
    /// constructor — before the window exists — so the width came from a literal 160. On anything
    /// narrower the cluster overflowed and the header wrapped onto a second row, which is what the
    /// maintainer's screenshot showed; it only straightened out when a resize rebuilt the markup.
    /// <para>
    /// No existing test could see it, and the reason is worth keeping: every render path in this app
    /// rebuilds the header markup on the way past — the demo scene a snapshot loads does it, a resize
    /// does it — and by then <c>_window.Width</c> is populated and the fallback never fires. So the
    /// header is read <em>before</em> anything is rendered, which is the only moment the first frame's
    /// width is still the guess. The rendered row count follows, as the consequence a user sees.
    /// </para>
    /// </summary>
    [Test]
    [Arguments(80)]
    [Arguments(100)]
    [Arguments(120)]
    [Arguments(140)]
    [Arguments(200)] // wider than the old literal too, so the fix is a measurement and not a smaller guess
    public async Task TheFirstFrameFitsTheTerminalItIsOn(int width)
    {
        Console.SetIn(TextReader.Null);
        var app = new SharpMUTermApp(DemoScene.Build(), Headless, new HeadlessConsoleDriver(width, 30));

        await Assert.That(app.HeaderMarkupWidth).IsLessThanOrEqualTo(width);
        await Assert.That(Frame(width, 30).Rows.Header).IsEqualTo(1);
    }
}
