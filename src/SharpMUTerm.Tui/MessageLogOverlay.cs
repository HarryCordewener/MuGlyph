using Microsoft.Extensions.Logging;
using SharpMUTerm.Core.Diagnostics;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;

namespace SharpMUTerm.Tui;

/// <summary>
/// The client message viewer (⌃P ▸ <c>Show client messages</c>): a modal list of everything the
/// diagnostics pipeline has recorded this run — the transient status-line notices that dismissed
/// themselves, and whatever the telnet stack logged around them — newest last, each with its time,
/// severity and source. <c>+</c>/<c>-</c> move the capture level, Esc closes.
/// <para>
/// Deliberately plain: a list, a level switch and a close key. No filtering, no selection, no search.
/// It is a debugging aid, and the alternative it replaces is the thing that must not happen — putting
/// client chrome into the output window, which writes it to the character's transcript on disk. The
/// level switch is here rather than in a settings screen because the workflow it serves is "turn
/// tracing on, reproduce it, look", and all three steps happen in front of this window.
/// </para>
/// </summary>
internal sealed class MessageLogOverlay
{
    /// <summary>The levels <c>+</c>/<c>-</c> step through, quietest last.</summary>
    private static readonly LogLevel[] Levels =
    {
        LogLevel.Trace, LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error,
    };

    private readonly ConsoleWindowSystem _system;
    private readonly ClientDiagnostics _diagnostics;

    private Window? _window;
    private MarkupControl? _content;

    public MessageLogOverlay(ConsoleWindowSystem system, ClientDiagnostics diagnostics)
    {
        _system = system;
        _diagnostics = diagnostics;
    }

    public bool IsOpen => _window is not null;

    /// <summary>Opens the viewer if closed, closes it if open (the ⌃P entry toggles, like the screens do).</summary>
    public void Toggle()
    {
        if (_window is not null)
        {
            Close();
            return;
        }

        var entries = _diagnostics.Messages.Entries;
        var desktop = _system.DesktopDimensions;
        var width = Math.Clamp(ClientMessageRenderer.MaxWidth(entries) + 3, 44, Math.Max(44, desktop.Width - 6));
        var height = Math.Clamp(entries.Count + 4, 8, Math.Max(8, desktop.Height - 2));

        _content = new MarkupControl(Lines(width - 3));

        // Centered() after WithSize(): the builder reads the bounds set so far and falls back to 80x25,
        // so centring first places the window as if it were that size (see CommandPalette).
        _window = new WindowBuilder(_system)
            .WithTitle("Client messages")
            .AsModal()
            .WithBorderStyle(BorderStyle.Single)
            .WithBackgroundColor(new Color(ScreenPalette.MenuBg))
            .HideTitleButtons()
            .Resizable(false)
            .WithSize(width, height)
            .Centered()
            .AddControl(_content)
            .OnClosed((_, _) => Reset())
            .Build();

        _window.PreviewKeyPressed += OnKey;
        _system.AddWindow(_window);
    }

    /// <summary>The header line plus one row per message, at the viewer's content width.</summary>
    private List<string> Lines(int width)
    {
        var lines = new List<string>
        {
            $"[{ScreenPalette.Label}]capturing[/] [{ScreenPalette.Accent}]{Level(_diagnostics.MinimumLevel)}[/]" +
            $" [{ScreenPalette.Label}]and louder · +/- level · Esc close[/]",
            string.Empty,
        };
        lines.AddRange(ClientMessageRenderer.Render(_diagnostics.Messages.Entries, width));
        return lines;
    }

    /// <summary>Steps the capture level and repaints, so the effect of the key is visible where it was pressed.</summary>
    internal void StepLevel(int delta)
    {
        var index = Array.IndexOf(Levels, _diagnostics.MinimumLevel);
        index = index < 0 ? Array.IndexOf(Levels, LogLevel.Information) : index;
        _diagnostics.MinimumLevel = Levels[Math.Clamp(index + delta, 0, Levels.Length - 1)];
        _content?.SetContent(Lines(_window is { } window ? Math.Max(20, window.Width - 3) : 80));
    }

    private static string Level(LogLevel level) => level.ToString().ToLowerInvariant();

    private void OnKey(object? sender, KeyPressedEventArgs e)
    {
        switch (e.KeyInfo.KeyChar)
        {
            case '+':
            case '=': // the unshifted key, since + needs a shift on most layouts
                StepLevel(-1); // louder: one level quieter in the list is one level more captured
                e.Handled = true;
                return;
            case '-':
                StepLevel(1);
                e.Handled = true;
                return;
        }

        if (e.KeyInfo.Key is ConsoleKey.Escape)
        {
            Close();
            e.Handled = true;
        }
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
        _content = null;
    }
}
