namespace SharpMUTerm.Core.Automation;

/// <summary>
/// A keybind: a normalised key descriptor (e.g. <c>Ctrl+F1</c>, <c>Alt+k</c>, <c>F3</c>) mapped
/// to a command to send, or a named script callback. The UI layer is responsible for
/// translating concrete key events into descriptor strings via <see cref="MacroKey"/>.
/// </summary>
public sealed class Macro
{
    /// <summary>
    /// What the binding is called, for the lists that show it. Settable so the F4 screen can rename one
    /// live; nothing is derived from it — <see cref="MacroEngine"/> is keyed by <see cref="Key"/>, never
    /// by name — so there is no cache to drop. <see cref="Key"/> deliberately stays <c>init</c>: it
    /// <em>is</em> the engine's key, and rebinding it needs a key-capture mode rather than a text field.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The normalised key descriptor that triggers this macro.</summary>
    public required string Key { get; init; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The command to send when the key is pressed. Settable so the F4 screen can edit it live; the
    /// engine reads it per press, so a change applies to the next one. Nothing is cached from it.
    /// </summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Optional named script callback (resolved by the scripting layer).</summary>
    public string? ScriptCallback { get; init; }
}

/// <summary>Builds and normalises key descriptor strings so bindings compare consistently.</summary>
public static class MacroKey
{
    /// <summary>
    /// Produces a canonical descriptor from modifier flags and a base key name, e.g.
    /// <c>Ctrl+Shift+F1</c>. Modifier order is always Ctrl, Alt, Shift.
    /// </summary>
    public static string Describe(string key, bool ctrl = false, bool alt = false, bool shift = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var parts = new List<string>(4);
        if (ctrl)
        {
            parts.Add("Ctrl");
        }

        if (alt)
        {
            parts.Add("Alt");
        }

        if (shift)
        {
            parts.Add("Shift");
        }

        parts.Add(key);
        return string.Join('+', parts);
    }
}
