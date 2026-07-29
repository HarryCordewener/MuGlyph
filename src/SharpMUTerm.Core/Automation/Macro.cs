using System.Globalization;

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
    /// live; nothing is derived from it — <see cref="MacroEngine"/> resolves on <see cref="Key"/>, never
    /// on the name — so there is no cache to drop.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The normalised key descriptor that triggers this macro. Settable so the F4 screen can rebind one
    /// live, through its key-capture mode rather than a text buffer.
    /// <para>
    /// It <em>is</em> what <see cref="MacroEngine"/> looks a keystroke up by, so it is precisely the
    /// property that must not be cached anywhere: the engine therefore reads it per press rather than
    /// holding a dictionary keyed on the string it was handed at construction, which would leave a
    /// rebound macro still answering to the key it no longer carries until the next reconnect. That is
    /// the same trap <see cref="Trigger.Pattern"/> and <see cref="Alias.CaseSensitive"/> guard against
    /// by dropping their compiled matcher on write.
    /// </para>
    /// </summary>
    public required string Key { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The command to send when the key is pressed. Settable so the F4 screen can edit it live; the
    /// engine reads it per press, so a change applies to the next one. Nothing is cached from it.
    /// </summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Optional named script callback (resolved by the scripting layer).</summary>
    public string? ScriptCallback { get; init; }
}

/// <summary>
/// A key descriptor taken apart: the modifiers held down, and the name of the key itself. Produced by
/// <see cref="MacroKey.TryParse"/> so a descriptor can be reasoned about — is it a function key, does
/// it carry Ctrl — without every caller re-splitting the string.
/// </summary>
/// <param name="Key">The base key's canonical name (<c>F1</c>, <c>K</c>, <c>Num5</c>, <c>Up</c>).</param>
/// <param name="Ctrl">Whether Ctrl is part of the chord.</param>
/// <param name="Alt">Whether Alt is part of the chord.</param>
/// <param name="Shift">Whether Shift is part of the chord.</param>
public readonly record struct MacroKeyParts(string Key, bool Ctrl, bool Alt, bool Shift);

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

    /// <summary>
    /// Splits a descriptor into its modifiers and its base key, settling the spelling of both. Modifier
    /// words are matched case-insensitively and may appear in any order (<c>shift+ctrl+f1</c> parses);
    /// the base key is normalised through <see cref="Normalise"/> so the several spellings a key is
    /// written with in the wild (<c>NumPad5</c>/<c>Num5</c>, <c>PgUp</c>/<c>PageUp</c>, <c>esc</c>)
    /// arrive as one.
    /// <para>
    /// Returns false for a descriptor with no base key, an empty component, or a word before the last
    /// <c>+</c> that names no modifier — a caller that cannot say what a descriptor <em>is</em> must not
    /// pretend it knows, because the answer decides whether a binding is drawn as one that fires.
    /// </para>
    /// </summary>
    public static bool TryParse(string? descriptor, out MacroKeyParts parts)
    {
        parts = default;
        if (string.IsNullOrWhiteSpace(descriptor))
        {
            return false;
        }

        var words = descriptor.Trim().Split('+');
        bool ctrl = false, alt = false, shift = false;
        for (var i = 0; i < words.Length - 1; i++)
        {
            switch (words[i].Trim().ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                default: return false;
            }
        }

        var key = words[^1].Trim();
        if (key.Length == 0)
        {
            return false;
        }

        parts = new MacroKeyParts(Normalise(key), ctrl, alt, shift);
        return true;
    }

    /// <summary>
    /// The canonical spelling of a descriptor — the form a capture writes and the form a stored binding
    /// is compared in — or null when it does not parse. <c>shift+ctrl+f1</c> and <c>Ctrl+Shift+F1</c>
    /// are the same binding and come back identical; <c>Num5</c> and <c>Ctrl+F1</c>, the two shapes
    /// already in configurations, come back untouched.
    /// </summary>
    public static string? Canonicalise(string? descriptor) =>
        TryParse(descriptor, out var parts) ? Describe(parts.Key, parts.Ctrl, parts.Alt, parts.Shift) : null;

    /// <summary>
    /// The canonical name of a base key. Letters upper-case, function keys <c>F1</c>–<c>F24</c>, numpad
    /// digits <c>Num0</c>–<c>Num9</c>, and one spelling each for the navigation and editing keys. A name
    /// this does not recognise is kept verbatim rather than rejected: a configuration may name a key this
    /// client has never heard of, and silently renaming it would be worse than leaving it alone.
    /// </summary>
    private static string Normalise(string key)
    {
        var lower = key.ToLowerInvariant();

        if (lower.Length is 1 && char.IsAsciiLetter(lower[0]))
        {
            return lower.ToUpperInvariant();
        }

        if ((Digits(lower, "numpad") ?? Digits(lower, "num")) is { } pad)
        {
            return "Num" + pad.ToString(CultureInfo.InvariantCulture);
        }

        if (Digits(lower, "f") is { } function && function is >= 1 and <= 24)
        {
            return "F" + function.ToString(CultureInfo.InvariantCulture);
        }

        return lower switch
        {
            "up" or "uparrow" => "Up",
            "down" or "downarrow" => "Down",
            "left" or "leftarrow" => "Left",
            "right" or "rightarrow" => "Right",
            "home" => "Home",
            "end" => "End",
            "pageup" or "pgup" => "PageUp",
            "pagedown" or "pgdn" or "pagedn" => "PageDown",
            "insert" or "ins" => "Insert",
            "delete" or "del" => "Delete",
            "enter" or "return" => "Enter",
            "escape" or "esc" => "Escape",
            "tab" => "Tab",
            "backspace" => "Backspace",
            "space" or "spacebar" => "Space",
            _ => key,
        };
    }

    /// <summary>The number after a prefix (<c>f11</c> → 11, <c>num5</c> → 5), or null when it isn't one.</summary>
    private static int? Digits(string lower, string prefix)
    {
        if (!lower.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = lower[prefix.Length..];
        return rest.Length > 0
               && int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
