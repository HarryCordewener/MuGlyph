using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace MuClient.Tui.Views;

/// <summary>
/// A single-line command entry with input history (Up/Down) and prefix tab-completion drawn
/// from prior commands. Enter raises <see cref="CommandEntered"/>.
/// </summary>
internal sealed class CommandInput : TextField
{
    private readonly List<string> _history = new();
    private int _historyIndex; // == _history.Count means "new, empty line"
    private string? _completionPrefix;
    private int _completionIndex;

    public CommandInput()
    {
        KeyDown += OnKeyDown;
    }

    /// <summary>Raised when the user presses Enter, carrying the entered command.</summary>
    public event Action<string>? CommandEntered;

    private void OnKeyDown(object? sender, Key key)
    {
        switch (key.KeyCode)
        {
            case KeyCode.Enter:
                Submit();
                key.Handled = true;
                break;

            case KeyCode.CursorUp:
                NavigateHistory(-1);
                key.Handled = true;
                break;

            case KeyCode.CursorDown:
                NavigateHistory(+1);
                key.Handled = true;
                break;

            case KeyCode.Tab:
                Complete();
                key.Handled = true;
                break;

            default:
                _completionPrefix = null; // any other key cancels a completion cycle
                break;
        }
    }

    private void Submit()
    {
        var command = Text ?? string.Empty;
        if (command.Length > 0)
        {
            _history.Remove(command);
            _history.Add(command);
        }

        _historyIndex = _history.Count;
        _completionPrefix = null;
        SetText(string.Empty);
        CommandEntered?.Invoke(command);
    }

    private void NavigateHistory(int direction)
    {
        if (_history.Count == 0)
        {
            return;
        }

        _historyIndex = Math.Clamp(_historyIndex + direction, 0, _history.Count);
        SetText(_historyIndex >= _history.Count ? string.Empty : _history[_historyIndex]);
    }

    private void Complete()
    {
        var text = Text ?? string.Empty;
        _completionPrefix ??= text;

        if (string.IsNullOrEmpty(_completionPrefix))
        {
            return;
        }

        var matches = _history
            .Where(h => h.StartsWith(_completionPrefix, StringComparison.OrdinalIgnoreCase) && h != _completionPrefix)
            .Distinct()
            .ToList();
        if (matches.Count == 0)
        {
            return;
        }

        _completionIndex %= matches.Count;
        SetText(matches[_completionIndex]);
        _completionIndex++;
    }

    private void SetText(string value)
    {
        Text = value;
        InsertionPoint = value.Length;
    }
}
