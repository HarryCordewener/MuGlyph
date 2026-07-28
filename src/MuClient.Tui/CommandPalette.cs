using MuClient.Core.Commands;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;

namespace MuClient.Tui;

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

        _window = new WindowBuilder(_system)
            .WithTitle("Command surface")
            .AsModal()
            .Centered()
            .WithBorderStyle(BorderStyle.Single)
            .WithSize(66, 18)
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
        _results?.SetContent(CommandSurfaceRenderer.Render(_ranked, _selected, _total, _context()));

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
