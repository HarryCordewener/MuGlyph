namespace SharpMUTerm.Tui;

/// <summary>
/// Which windows are showing their second command line. The maintainer's requirement is one line long
/// — "Toggleable PER Window!" — and it is the whole reason this is not a single boolean on the app:
/// the bar you keep OOC in belongs to the window you keep OOC in, and following the active tab is what
/// makes it worth having.
/// <para>
/// A window with no answer of its own takes F8's, read live rather than copied at construction, so
/// ticking the box changes what the next window does without a restart. Toggling records an answer for
/// that window only, so a window that was told once keeps what it was told even if the default flips
/// underneath it.
/// </para>
/// </summary>
internal sealed class InputBarVisibility(Func<bool> byDefault)
{
    private readonly Dictionary<string, bool> _shown = new(StringComparer.Ordinal);
    private readonly Func<bool> _byDefault = byDefault;

    /// <summary>Whether this window shows its second bar.</summary>
    internal bool IsShown(string windowId)
    {
        ArgumentNullException.ThrowIfNull(windowId);
        return _shown.TryGetValue(windowId, out var shown) ? shown : _byDefault();
    }

    /// <summary>Flips one window's second bar and reports what it became.</summary>
    internal bool Toggle(string windowId)
    {
        ArgumentNullException.ThrowIfNull(windowId);
        var shown = !IsShown(windowId);
        _shown[windowId] = shown;
        return shown;
    }

    /// <summary>Forgets a closed window's answer, so a same-id window later starts from the default.</summary>
    internal void Forget(string windowId)
    {
        ArgumentNullException.ThrowIfNull(windowId);
        _shown.Remove(windowId);
    }
}
