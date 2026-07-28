namespace SharpMUTerm.Tui;

/// <summary>
/// A checkbox row on a settings screen, bound to the config it shows: how to read the flag, how to
/// flip it, and how to put back exactly what was there before. The snapshot exists because not every
/// checkbox is a plain <c>bool</c> property — F9's "auto-start on connect" is really a
/// <see cref="SharpMUTerm.Core.Configuration.LogFormat"/>, and Esc has to restore <c>Html</c>, not
/// merely "on".
/// </summary>
/// <param name="Get">Reads the flag as the renderer draws it.</param>
/// <param name="Flip">Inverts the flag.</param>
/// <param name="Snapshot">Captures the current value, returning the action that restores it.</param>
internal readonly record struct ScreenToggle(Func<bool> Get, Action Flip, Func<Action> Snapshot)
{
    /// <summary>Binds a plain boolean property; restoring it is just writing the old value back.</summary>
    internal static ScreenToggle Bind(Func<bool> get, Action<bool> set)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);

        return new ScreenToggle(
            get,
            () => set(!get()),
            () =>
            {
                var previous = get();
                return () => set(previous);
            });
    }
}

/// <summary>
/// The navigable shape of one settings screen: its panes, each an ordered list of rows, where a row
/// is either a plain stop (<c>null</c> — selectable, but nothing to press) or a checkbox bound to
/// config. It carries no markup and no controls: the renderers draw what the cursor is on, this says
/// where the cursor may go and what happens there. Rebuilt from live config on every key, so it never
/// goes stale against a list the last keystroke changed.
/// </summary>
internal sealed class ScreenModel
{
    private readonly IReadOnlyList<ScreenToggle?>[] _panes;

    internal ScreenModel(params IReadOnlyList<ScreenToggle?>[] panes)
    {
        ArgumentNullException.ThrowIfNull(panes);
        _panes = panes.Length == 0 ? new IReadOnlyList<ScreenToggle?>[] { Array.Empty<ScreenToggle?>() } : panes;
        Sizes = Array.ConvertAll(_panes, p => p.Count);
    }

    /// <summary>Row counts per pane, in pane order — what <see cref="ScreenSelection"/> navigates by.</summary>
    internal IReadOnlyList<int> Sizes { get; }

    /// <summary>How many panes the screen offers ⇥ between.</summary>
    internal int PaneCount => _panes.Length;

    /// <summary>The checkbox at a cursor position, or null when that row isn't pressable.</summary>
    internal ScreenToggle? ToggleAt(int pane, int index) =>
        pane >= 0 && pane < _panes.Length && index >= 0 && index < _panes[pane].Count
            ? _panes[pane][index]
            : null;

    /// <summary>A pane of rows that are selectable but carry no checkbox (a plain list).</summary>
    internal static IReadOnlyList<ScreenToggle?> Stops(int count) => new ScreenToggle?[Math.Max(0, count)];

    /// <summary>A pane built by binding one checkbox per item of <paramref name="items"/>.</summary>
    internal static IReadOnlyList<ScreenToggle?> Toggles<T>(
        IReadOnlyList<T> items, Func<T, bool> get, Action<T, bool> set)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);

        var rows = new ScreenToggle?[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            rows[i] = ScreenToggle.Bind(() => get(item), value => set(item, value));
        }

        return rows;
    }
}
