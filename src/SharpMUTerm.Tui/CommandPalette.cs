using SharpMUTerm.Core.Commands;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;

namespace SharpMUTerm.Tui;

/// <summary>
/// The command surface (⌃P): a modal search box over a live-generated catalog, ranked with
/// <see cref="CommandMatcher"/> and rendered grouped by <see cref="CommandSurfaceRenderer"/>. ↑↓
/// walk the flattened results, ⏎ dispatches the selected id, Esc closes. The catalog and dispatch
/// are supplied by the app so this stays a thin view over tested logic.
/// </summary>
internal sealed class CommandPalette
{
    private readonly ConsoleWindowSystem _system;
    private readonly Func<IReadOnlyList<CommandItem>> _catalog;
    private readonly Func<string?> _context;
    private readonly Action<string> _dispatch;

    private Window? _window;
    private PromptControl? _search;
    private MarkupControl? _results;
    private IReadOnlyList<RankedCommand> _ranked = Array.Empty<RankedCommand>();
    private IReadOnlyList<CommandItem> _ordered = Array.Empty<CommandItem>();
    private int _total;
    private int _selected;
    private int _contentWidth;

    public CommandPalette(
        ConsoleWindowSystem system,
        Func<IReadOnlyList<CommandItem>> catalog,
        Func<string?> context,
        Action<string> dispatch)
    {
        _system = system;
        _catalog = catalog;
        _context = context;
        _dispatch = dispatch;
    }

    public bool IsOpen => _window is not null;

    /// <summary>Opens the surface if closed, closes it if open (bound to ⌃P).</summary>
    public void Toggle()
    {
        if (_window is not null)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    private void Open()
    {
        _selected = 0;
        _search = Controls.Prompt("›").Build();
        _search.InputChanged += (_, query) => Refresh(query);
        _search.Entered += (_, _) => Commit();

        _results = new MarkupControl(new List<string>());

        // Size the surface to hug the full (unfiltered) catalog so nothing scrolls and there's no empty
        // gutter: fit height to the row count and width to the widest row, both capped to the desktop.
        var catalog = _catalog();
        var initial = CommandSurfaceRenderer.Render(
            CommandMatcher.Rank(string.Empty, catalog), 0, catalog.Count, _context());
        var desktop = _system.DesktopDimensions;
        var height = Math.Clamp(initial.Count + 3, 6, Math.Max(6, desktop.Height - 2));
        _contentWidth = Math.Clamp(CommandSurfaceRenderer.MaxWidth(initial) + 1, 32, Math.Max(32, desktop.Width - 6));
        var width = _contentWidth + 2; // + the 1-cell left/right border

        // A clean overlay: single border, but no window chrome — no minimize/maximize/close buttons
        // (HideTitleButtons) and no resize grip (Resizable(false)); Esc closes it. It floats on
        // ScreenPalette.MenuBg — the tone the settings screens already reserve for a list drawn *over*
        // the rows beneath it — so the surface lifts off the workspace panes rather than matching them.
        // Centered() *after* WithSize(): it reads the bounds set so far and falls back to 80x25, so
        // centring first placed the surface as if it were that size — which only became visible once
        // the catalog grew tall enough for the misplacement to push its bottom border off-screen.
        _window = new WindowBuilder(_system)
            .WithTitle("Command surface")
            .AsModal()
            .WithBorderStyle(BorderStyle.Single)
            .WithBackgroundColor(new Color(ScreenPalette.MenuBg))
            .HideTitleButtons()
            .Resizable(false)
            .WithSize(width, height)
            .Centered()
            .AddControl(_search)
            .AddControl(_results)
            .OnClosed((_, _) => Reset())
            .Build();

        _window.PreviewKeyPressed += OnKey;
        _system.AddWindow(_window);
        Refresh(string.Empty);
    }

    private void Refresh(string query)
    {
        var catalog = _catalog();
        _total = catalog.Count;
        _ranked = CommandMatcher.Rank(query, catalog);
        _ordered = CommandSurfaceRenderer.Order(_ranked);
        _selected = _ordered.Count == 0 ? -1 : Math.Clamp(_selected, 0, _ordered.Count - 1);
        Paint();
    }

    private void Paint() =>
        _results?.SetContent(CommandSurfaceRenderer.Render(_ranked, _selected, _total, _context(), _contentWidth));

    private void Move(int delta)
    {
        if (_ordered.Count == 0)
        {
            return;
        }

        _selected = ((_selected + delta) % _ordered.Count + _ordered.Count) % _ordered.Count;
        Paint();
    }

    private void Commit()
    {
        if (_selected >= 0 && _selected < _ordered.Count)
        {
            var id = _ordered[_selected].Id;
            Close();
            _dispatch(id);
        }
    }

    private void OnKey(object? sender, KeyPressedEventArgs e)
    {
        switch (e.KeyInfo.Key)
        {
            case ConsoleKey.DownArrow:
                Move(1);
                e.Handled = true;
                break;
            case ConsoleKey.UpArrow:
                Move(-1);
                e.Handled = true;
                break;
            case ConsoleKey.Enter:
                Commit();
                e.Handled = true;
                break;
            case ConsoleKey.Escape:
                Close();
                e.Handled = true;
                break;
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
        _search = null;
        _results = null;
        _ranked = Array.Empty<RankedCommand>();
        _ordered = Array.Empty<CommandItem>();
    }
}
