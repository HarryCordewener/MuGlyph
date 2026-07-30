using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using static SharpMUTerm.Tui.MarkupText;

namespace SharpMUTerm.Tui;

/// <summary>
/// The modal that asks whether to keep the deletions a settings screen made, put up as that screen
/// closes. It is the host and nothing else — <see cref="ScreenEditReview"/> owns what a keystroke means
/// and what the question says — which is the split <see cref="QuitOverlay"/> and
/// <see cref="SettingsOverlay"/> already use, and the only reason any of this is testable without a
/// terminal.
/// <para>
/// It opens <em>after</em> the settings window has gone rather than on top of it, which is worth stating
/// because the alternative looks tidier and isn't. Keys reach a modal through the active window's
/// <c>PreviewKeyPressed</c>; a second modal stacked over a screen that is itself listening there would
/// leave "which of the two hears Delete" as a property of the framework's window ordering that this
/// project cannot drive a key through headlessly to check. Closing first makes the review the only
/// window in play, which is the arrangement every test and every snapshot here can actually verify. It
/// also matches what the user is doing: they asked to leave, and this is the last question on the way out.
/// </para>
/// </summary>
internal sealed class EditReviewOverlay
{
    /// <summary>The narrowest the surface goes, whatever was deleted.</summary>
    private const int MinimumWidth = 38;

    private readonly ConsoleWindowSystem _system;

    private Window? _window;
    private MarkupControl? _body;
    private IReadOnlyList<string> _deletions = Array.Empty<string>();
    private ReviewChoice _selected = ReviewChoice.Keep;
    private Action? _keep;
    private Action? _undo;

    public EditReviewOverlay(ConsoleWindowSystem system) => _system = system;

    public bool IsOpen => _window is not null;

    /// <summary>What the prompt is currently showing, for a headless test to read back.</summary>
    internal IReadOnlyList<string> Lines => ScreenEditReview.Render(_deletions, _selected);

    /// <summary>
    /// Asks about <paramref name="deletions"/>, running <paramref name="keep"/> or
    /// <paramref name="undo"/> on the answer. The overlay performs neither itself: what "put them back"
    /// means belongs to the <see cref="ScreenEdits"/> log that recorded them.
    /// </summary>
    public void Open(IReadOnlyList<string> deletions, Action keep, Action undo)
    {
        ArgumentNullException.ThrowIfNull(deletions);

        if (_window is not null || deletions.Count == 0)
        {
            return;
        }

        _deletions = deletions;
        _keep = keep;
        _undo = undo;
        _selected = ReviewChoice.Keep;

        var lines = Lines;
        _body = new MarkupControl(new List<string>(lines));

        // Hug the content: the question is as tall as it has things to name. Centred *after* WithSize,
        // because the builder reads the bounds set so far and falls back to 80x25 otherwise.
        var desktop = _system.DesktopDimensions;
        var content = lines.Max(VisibleLength) + 4; // the 1-cell border each side, plus a cell of margin
        var width = Math.Clamp(content, MinimumWidth, Math.Max(MinimumWidth, desktop.Width - 6));
        var height = Math.Clamp(lines.Count + 2, 6, Math.Max(6, desktop.Height - 2));

        _window = new WindowBuilder(_system)
            .WithTitle("Deletions")
            .AsModal()
            .WithBorderStyle(BorderStyle.Single)
            .WithBackgroundColor(new Color(ScreenPalette.MenuBg))
            .HideTitleButtons()
            .Resizable(false)
            .WithSize(width, height)
            .Centered()
            .AddControl(_body)
            .OnClosed((_, _) => Reset())
            .Build();

        _window.PreviewKeyPressed += OnKey;
        _system.AddWindow(_window);
    }

    /// <summary>
    /// Feeds one key to the very handler <c>PreviewKeyPressed</c> raises, for the same reason
    /// <see cref="SettingsOverlay.SimulateKey"/> and <see cref="QuitOverlay.SimulateKey"/> exist: the
    /// framework only pumps keys inside <c>Run()</c>, which a headless test never enters.
    /// </summary>
    internal void SimulateKey(ConsoleKeyInfo key) => OnKey(this, new KeyPressedEventArgs(key, false));

    private void OnKey(object? sender, KeyPressedEventArgs e)
    {
        if (_window is null)
        {
            return;
        }

        var decision = ScreenEditReview.Interpret(e.KeyInfo, _selected);
        _selected = decision.Selection;
        e.Handled = true;

        switch (decision.Action)
        {
            case ReviewAction.Keep:
                Answer(_keep);
                break;

            case ReviewAction.Undo:
                Answer(_undo);
                break;

            case ReviewAction.Redraw:
                _body?.SetContent(new List<string>(Lines));
                _window.Invalidate(redrawAll: true);
                break;

            case ReviewAction.None:
            default:
                break;
        }
    }

    /// <summary>
    /// Closes first, then acts: the answer is about a screen that has already gone, and leaving the
    /// question painted while its consequence runs would be the last thing on screen.
    /// </summary>
    private void Answer(Action? answer)
    {
        Close();
        answer?.Invoke();
    }

    private void Close()
    {
        if (_window is { } window)
        {
            _system.CloseModalWindow(window);
        }
    }

    private void Reset()
    {
        _window = null;
        _body = null;
        _keep = null;
        _undo = null;
        _deletions = Array.Empty<string>();
    }
}
