using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpMUTerm.Core.Input;

namespace SharpMUTerm.Tui;

/// <summary>
/// The history surface (⌃R): a modal list of everything entered on the armed command line, newest first,
/// which typing filters. It is the host and nothing else — <see cref="HistorySearchPrompt"/> owns what a
/// keystroke means and what the surface says, and <see cref="HistorySearch"/> owns the filtering, which is
/// the split <see cref="QuitOverlay"/> and <see cref="SettingsOverlay"/> already use and the only reason
/// any of this is testable without a terminal.
/// <para>
/// Keys arrive on <c>PreviewKeyPressed</c>, before any control sees them, exactly as they do for the quit
/// prompt and the settings screens. There is no framework input control here for the same reason those
/// have none: the query is a buffer this class owns and the whole body is markup redrawn on every key, so
/// there is nothing for focus to land on and nothing to keep in step. Every key is marked handled — a
/// list floating over the workspace that let typing through would be filling in the command line it is
/// covering.
/// </para>
/// <para>
/// It is per command line, because history is: the primary and second bars keep separate lists
/// (<c>SharpMUTermApp.HistoryFor</c>), and this surface reads and writes whichever one is armed at the
/// moment it opens.
/// </para>
/// </summary>
internal sealed class HistorySurface
{
    /// <summary>
    /// The narrowest the surface goes, whatever the entries turn out to be. It is set by the footer: a
    /// surface too narrow for its own key hints would wrap them onto a second row and push the list up,
    /// which is the one line here that must always be readable. Checked by test against
    /// <see cref="HistorySearchPrompt.Hints"/> rather than trusted.
    /// </summary>
    internal const int MinimumWidth = 62;

    /// <summary>
    /// The rows the surface spends on something other than history: the query line, the blank under it, the
    /// blank above the footer, and the footer. Keep in step with <see cref="HistorySearchPrompt.Render"/>,
    /// which is where they are actually emitted.
    /// </summary>
    private const int ChromeRows = 4;

    private readonly ConsoleWindowSystem _system;
    private readonly Func<IReadOnlyList<string>> _entries;
    private readonly Func<string> _label;
    private readonly Action<int> _insert;

    private Window? _window;
    private MarkupControl? _body;
    private IReadOnlyList<string> _snapshot = Array.Empty<string>();
    private IReadOnlyList<HistoryMatch> _matches = Array.Empty<HistoryMatch>();
    private string _query = string.Empty;
    private string _barLabel = string.Empty;
    private int _selected;
    private int _contentWidth;

    /// <summary>How many rows of list the surface has room for, and which match is at the top of them.</summary>
    private int _listRows;
    private int _first;

    /// <summary>
    /// <paramref name="entries"/> and <paramref name="label"/> are read at the moment the surface opens —
    /// the list is the one belonging to the command line the keystroke arrived on.
    /// <paramref name="insert"/> is handed an index into that list, not the text: the host puts it on the
    /// command line through <see cref="InputHistory.RecallAt"/>, so the displaced draft is parked exactly
    /// as <c>↑</c> parks it. Nothing here sends anything.
    /// </summary>
    public HistorySurface(
        ConsoleWindowSystem system,
        Func<IReadOnlyList<string>> entries,
        Func<string> label,
        Action<int> insert)
    {
        _system = system;
        _entries = entries;
        _label = label;
        _insert = insert;
    }

    public bool IsOpen => _window is not null;

    /// <summary>What the surface is currently showing, for a headless test to read back.</summary>
    internal IReadOnlyList<string> Lines => HistorySearchPrompt.Render(
        _matches, _query, _selected, _snapshot.Count, _barLabel, _contentWidth - 1, _listRows, _first);

    /// <summary>The filter as typed so far.</summary>
    internal string Query => _query;

    /// <summary>The rows currently listed, newest first — what ↑↓ walk and ⏎ picks from.</summary>
    internal IReadOnlyList<HistoryMatch> Matches => _matches;

    /// <summary>The entry ⏎ would insert, or null when nothing is listed.</summary>
    internal string? Selected =>
        _selected >= 0 && _selected < _matches.Count ? _matches[_selected].Text : null;

    /// <summary>
    /// Opens the surface, or closes it when ⌃R arrives a second time. A global shortcut runs before any
    /// window, so the running client never reaches this surface's own handler with ⌃R — this is where that
    /// key is answered, and it answers it the way <see cref="HistorySearchPrompt.Interpret"/> says:
    /// closing, like every other surface in this client does on the chord that opened it.
    /// </summary>
    public void Toggle()
    {
        if (_window is not null)
        {
            Close();
            return;
        }

        Open();
    }

    /// <summary>Opens the surface into a headless frame (used by the <c>history-search</c> snapshot views).</summary>
    public void OpenForSnapshot() => Open();

    /// <summary>
    /// Feeds one key to the very handler <c>PreviewKeyPressed</c> raises, for the same reason
    /// <see cref="SettingsOverlay.SimulateKey"/> exists: the framework only pumps keys inside
    /// <c>Run()</c>, which a headless test or snapshot never enters.
    /// </summary>
    public void SimulateKey(ConsoleKeyInfo key) => OnKey(this, new KeyPressedEventArgs(key, false));

    /// <summary>Types a whole query in, one real keystroke at a time.</summary>
    public void SimulateTyping(string text)
    {
        foreach (var c in text)
        {
            SimulateKey(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false));
        }
    }

    private void Open()
    {
        _snapshot = _entries();
        _barLabel = _label();
        _query = string.Empty;
        _selected = _snapshot.Count == 0 ? -1 : 0;
        _matches = HistorySearch.Match(_snapshot, _query);

        // Size once, to the unfiltered list, and never again: filtering pads the list area rather than
        // shrinking the window, so the rows and the footer stay where the eye left them. The row count is
        // capped to what the desktop can hold, and Render scrolls the list inside that — a five-hundred
        // entry history has to be walkable on a short terminal, not merely present.
        var desktop = _system.DesktopDimensions;
        _listRows = Math.Max(1, Math.Min(Math.Max(_snapshot.Count, 1), desktop.Height - ChromeRows - 2));
        _first = 0;

        var initial = HistorySearchPrompt.Render(
            _matches, _query, _selected, _snapshot.Count, _barLabel, 0, _listRows, _first);
        // + 2: one cell of margin inside each border, the same hug QuitOverlay uses. Everything is then
        // laid out to _contentWidth - 1, so the right-aligned count has a cell between it and the frame
        // instead of leaning on it, and the selected row's bar stops there too.
        _contentWidth = Math.Clamp(
            HistorySearchPrompt.MaxWidth(initial) + 2, MinimumWidth, Math.Max(MinimumWidth, desktop.Width - 6));
        var width = _contentWidth + 2; // + the 1-cell left/right border
        var height = Math.Min(_listRows + ChromeRows + 2, Math.Max(ChromeRows + 3, desktop.Height - 2));

        _body = new MarkupControl(new List<string>());

        // The same clean overlay the command surface uses: single border, no title buttons, no resize
        // grip, floating on the tone reserved for a list drawn *over* the rows beneath it. Centred
        // *after* WithSize, because the builder reads the bounds set so far and falls back to 80x25.
        _window = new WindowBuilder(_system)
            .WithTitle("Command history")
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
        Paint();
    }

    private void OnKey(object? sender, KeyPressedEventArgs e)
    {
        if (_window is null)
        {
            return;
        }

        var decision = HistorySearchPrompt.Interpret(e.KeyInfo, _query, _selected, _matches.Count);
        e.Handled = true;

        switch (decision.Action)
        {
            case HistoryAction.Insert:
                // Closed first: the entry lands on a command line that is about to be the only thing on
                // screen, and inserting under a modal that is still painted would hide what just happened.
                var index = _matches[_selected].Index;
                Close();
                _insert(index);
                break;

            case HistoryAction.Cancel:
                Close();
                break;

            case HistoryAction.Redraw:
                Refilter(decision.Query, decision.Selected);
                Paint();
                _window.Invalidate(redrawAll: true);
                break;

            case HistoryAction.None:
            default:
                break;
        }
    }

    /// <summary>
    /// Applies a new query and pointer. The pointer is clamped to what the query actually matched: a
    /// filter that narrowed the list must not leave ⏎ aimed past the end of it.
    /// </summary>
    private void Refilter(string query, int selected)
    {
        _query = query;
        _matches = HistorySearch.Match(_snapshot, _query);
        _selected = _matches.Count == 0 ? -1 : Math.Clamp(selected, 0, _matches.Count - 1);
        _first = HistorySearchPrompt.Scroll(_first, _selected, _matches.Count, _listRows);
    }

    private void Paint() => _body?.SetContent(new List<string>(Lines));

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
    }
}
