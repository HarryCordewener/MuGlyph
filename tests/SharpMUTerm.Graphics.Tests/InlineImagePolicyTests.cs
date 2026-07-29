using SharpMUTerm.Graphics;

namespace SharpMUTerm.Graphics.Tests;

/// <summary>
/// The degradation chain is the part of the graphics layer a headless run can actually verify, so it
/// is covered exhaustively: every <see cref="GraphicsProtocol"/> against every surface shape.
/// </summary>
public class InlineImagePolicyTests
{
    private static TerminalCapabilities Caps(GraphicsProtocol protocol) =>
        new(protocol,
            supportsTrueColor: protocol >= GraphicsProtocol.HalfBlock,
            supportsKittyGraphics: protocol == GraphicsProtocol.Kitty,
            supportsSixel: protocol == GraphicsProtocol.Sixel);

    // ---- The full protocol × surface matrix -------------------------------------------------

    [Test]
    [Arguments(GraphicsProtocol.Kitty, InlineImagePresentation.Kitty)]
    [Arguments(GraphicsProtocol.Sixel, InlineImagePresentation.Sixel)]
    [Arguments(GraphicsProtocol.HalfBlock, InlineImagePresentation.HalfBlock)]
    [Arguments(GraphicsProtocol.None, InlineImagePresentation.TextPlaceholder)]
    public async Task RawTerminal_UsesEveryProtocolAsDetected(
        GraphicsProtocol protocol, InlineImagePresentation expected)
    {
        var chosen = InlineImagePolicy.Select(Caps(protocol), GraphicsSurface.RawTerminal);
        await Assert.That(chosen).IsEqualTo(expected);
    }

    [Test]
    [Arguments(GraphicsProtocol.Kitty, InlineImagePresentation.Kitty)]
    [Arguments(GraphicsProtocol.Sixel, InlineImagePresentation.HalfBlock)]
    [Arguments(GraphicsProtocol.HalfBlock, InlineImagePresentation.HalfBlock)]
    [Arguments(GraphicsProtocol.None, InlineImagePresentation.TextPlaceholder)]
    public async Task KittyCapableCompositor_CarriesKittyButDropsSixelToHalfBlock(
        GraphicsProtocol protocol, InlineImagePresentation expected)
    {
        var chosen = InlineImagePolicy.Select(Caps(protocol), GraphicsSurface.Compositor(canPlaceKitty: true));
        await Assert.That(chosen).IsEqualTo(expected);
    }

    [Test]
    [Arguments(GraphicsProtocol.Kitty, InlineImagePresentation.HalfBlock)]
    [Arguments(GraphicsProtocol.Sixel, InlineImagePresentation.HalfBlock)]
    [Arguments(GraphicsProtocol.HalfBlock, InlineImagePresentation.HalfBlock)]
    [Arguments(GraphicsProtocol.None, InlineImagePresentation.TextPlaceholder)]
    public async Task CompositorWithoutKittyDriver_AlwaysLandsOnHalfBlock(
        GraphicsProtocol protocol, InlineImagePresentation expected)
    {
        var chosen = InlineImagePolicy.Select(Caps(protocol), GraphicsSurface.Compositor(canPlaceKitty: false));
        await Assert.That(chosen).IsEqualTo(expected);
    }

    [Test]
    [Arguments(GraphicsProtocol.Kitty)]
    [Arguments(GraphicsProtocol.Sixel)]
    [Arguments(GraphicsProtocol.HalfBlock)]
    [Arguments(GraphicsProtocol.None)]
    public async Task PlainTextSurface_NeverDrawsAnything(GraphicsProtocol protocol)
    {
        var chosen = InlineImagePolicy.Select(Caps(protocol), GraphicsSurface.PlainText);
        await Assert.That(chosen).IsEqualTo(InlineImagePresentation.TextPlaceholder);
    }

    // ---- Invariants over the whole matrix ---------------------------------------------------

    [Test]
    public async Task ChosenPresentation_NeverExceedsWhatTheTerminalDetected()
    {
        var surfaces = new[]
        {
            GraphicsSurface.RawTerminal,
            GraphicsSurface.Compositor(canPlaceKitty: true),
            GraphicsSurface.Compositor(canPlaceKitty: false),
            GraphicsSurface.PlainText,
        };

        foreach (var protocol in Enum.GetValues<GraphicsProtocol>())
        {
            foreach (var surface in surfaces)
            {
                var chosen = InlineImagePolicy.Select(Caps(protocol), surface);
                await Assert.That((int)chosen)
                    .IsLessThanOrEqualTo((int)protocol)
                    .Because($"{protocol} on this surface must not upgrade to {chosen}");
            }
        }
    }

    [Test]
    public async Task NoProtocol_AlwaysEndsAtTheTextPlaceholder()
    {
        // The sandbox — and any pipe, dumb terminal, or CI job — is exactly this case.
        var chosen = InlineImagePolicy.Select(Caps(GraphicsProtocol.None), GraphicsSurface.RawTerminal);
        await Assert.That(chosen).IsEqualTo(InlineImagePresentation.TextPlaceholder);
    }

    // ---- The explicit override still wins ---------------------------------------------------

    [Test]
    public async Task ForcedKitty_IsHonouredEvenWhenNoFlagWasSniffed()
    {
        // SHARPMUTERM_GRAPHICS=kitty forces Protocol without setting SupportsKittyGraphics; the
        // policy reads Protocol, so the user's override is not quietly discarded.
        var forced = new TerminalCapabilities(
            GraphicsProtocol.Kitty, supportsTrueColor: false, supportsKittyGraphics: false, supportsSixel: false);

        var chosen = InlineImagePolicy.Select(forced, GraphicsSurface.Compositor(canPlaceKitty: true));
        await Assert.That(chosen).IsEqualTo(InlineImagePresentation.Kitty);
    }

    [Test]
    public async Task ForcedNone_SuppressesGraphicsOnAFullyCapableTerminal()
    {
        var forced = new TerminalCapabilities(
            GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: true, supportsSixel: true);

        var chosen = InlineImagePolicy.Select(forced, GraphicsSurface.RawTerminal);
        await Assert.That(chosen).IsEqualTo(InlineImagePresentation.TextPlaceholder);
    }

    [Test]
    public async Task OverrideFlowsFromTheProbeThroughThePolicy()
    {
        // End to end over the real probe: env → capabilities → presentation.
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["SHARPMUTERM_GRAPHICS"] = "halfblock",
            ["TERM"] = "xterm-kitty",
        };

        var caps = CapabilityProbe.Detect(env);
        var chosen = InlineImagePolicy.Select(caps, GraphicsSurface.Compositor(canPlaceKitty: true));
        await Assert.That(chosen).IsEqualTo(InlineImagePresentation.HalfBlock);
    }

    // ---- Descriptions -----------------------------------------------------------------------

    [Test]
    public async Task Describe_NamesSixelAsTheReasonForDegradingInsideTheCompositor()
    {
        var text = InlineImagePolicy.Describe(
            Caps(GraphicsProtocol.Sixel), GraphicsSurface.Compositor(canPlaceKitty: false));

        await Assert.That(text).Contains("HalfBlock");
        await Assert.That(text).Contains("Sixel cannot be drawn inside the compositor");
    }

    [Test]
    public async Task Describe_ReportsAPlainMatchWithoutAnExcuse()
    {
        var text = InlineImagePolicy.Describe(
            Caps(GraphicsProtocol.Kitty), GraphicsSurface.Compositor(canPlaceKitty: true));

        await Assert.That(text).Contains("Kitty");
        await Assert.That(text).DoesNotContain("(");
    }

    [Test]
    public async Task Describe_SaysNothingWasDetectedWhenTheTerminalIsBare()
    {
        var text = InlineImagePolicy.Describe(Caps(GraphicsProtocol.None), GraphicsSurface.RawTerminal);
        await Assert.That(text).Contains("no inline graphics detected");
    }

    [Test]
    public async Task Describe_DistinguishesACapableTerminalBehindAnIncapableView()
    {
        var text = InlineImagePolicy.Describe(Caps(GraphicsProtocol.Kitty), GraphicsSurface.PlainText);
        await Assert.That(text).Contains("cannot draw images");
    }

    [Test]
    public async Task Select_RejectsNullArguments()
    {
        await Assert.That(() => InlineImagePolicy.Select(null!, GraphicsSurface.RawTerminal))
            .Throws<ArgumentNullException>();
        await Assert.That(() => InlineImagePolicy.Select(Caps(GraphicsProtocol.Kitty), null!))
            .Throws<ArgumentNullException>();
    }
}
