using System.Globalization;
using System.Text.RegularExpressions;

namespace SharpMUTerm.Tui;

/// <summary>
/// A field edit in flight, as the renderers need to draw it: which of the focused row's fields is
/// open, the buffer being typed, where the caret sits inside it, and why the last commit was refused
/// (null while nothing has been rejected). It carries the buffer rather than the field because the
/// buffer is deliberately *not* in config — an invalid value must never reach it, so what is being
/// typed lives in the session until it validates.
/// </summary>
/// <param name="Field">Which of the row's fields is open, in the row's own field order.</param>
/// <param name="Text">The buffer being typed.</param>
/// <param name="Caret">The caret's index into <paramref name="Text"/> (may equal its length).</param>
/// <param name="Error">Why the last commit was refused, or null.</param>
/// <param name="RowFields">
/// How many fields the row holds — the chrome only offers ⇥ when there is a next field to step to.
/// </param>
internal readonly record struct ScreenFieldEdit(
    int Field, string Text, int Caret, string? Error, int RowFields = 1);

/// <summary>
/// An editable value on a settings row: how to read it as text, whether a typed string is a legal
/// value for it, how to write it, and how to put back exactly what was there before. It follows
/// <see cref="ScreenToggle"/>'s Get / write / Snapshot shape for the same reason — the snapshot
/// captures the *typed* value, so undo restores an <c>int</c> port or a <c>LogFormat</c> rather than
/// the string it was displayed as.
/// <para>
/// Validation is deliberately split from writing: a screen validates a buffer before it is applied,
/// so a rejected value is refused at the field rather than parsed into config and corrected
/// afterwards. <see cref="Choices"/> is set for the enum-like fields, which additionally cycle with
/// ↑↓ while the edit is open.
/// </para>
/// </summary>
/// <param name="Label">What the field is called, used in its rejection messages.</param>
/// <param name="Get">Reads the current value as the text an edit opens on.</param>
/// <param name="Validate">Returns null when a buffer is a legal value, else why it isn't.</param>
/// <param name="Set">Writes a buffer that <paramref name="Validate"/> has already accepted.</param>
/// <param name="Snapshot">Captures the current value, returning the action that restores it.</param>
/// <param name="Choices">The legal values when the field is an enumeration, else null.</param>
internal readonly record struct ScreenField(
    string Label,
    Func<string> Get,
    Func<string, string?> Validate,
    Action<string> Set,
    Func<Action> Snapshot,
    IReadOnlyList<string>? Choices = null)
{
    /// <summary>Longest rejection message kept; regex parser errors run to several lines otherwise.</summary>
    private const int MaxErrorLength = 44;

    /// <summary>
    /// The choice <paramref name="direction"/> steps from <paramref name="current"/>, wrapping at both
    /// ends — how ↑↓ move through an enum field. Null when the field isn't an enumeration. A buffer
    /// that isn't one of the choices (half-typed) steps from the start.
    /// </summary>
    internal string? Cycle(string current, int direction)
    {
        if (Choices is not { Count: > 0 } choices)
        {
            return null;
        }

        var at = -1;
        for (var i = 0; i < choices.Count; i++)
        {
            if (string.Equals(choices[i], current, StringComparison.OrdinalIgnoreCase))
            {
                at = i;
                break;
            }
        }

        var next = at < 0 ? (direction > 0 ? 0 : choices.Count - 1) : at + direction;
        return choices[((next % choices.Count) + choices.Count) % choices.Count];
    }

    /// <summary>Free text that may not be blank — a name, a host, a dictionary. Trimmed on commit.</summary>
    internal static ScreenField Text(string label, Func<string> get, Action<string> set)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);

        return new ScreenField(
            label,
            get,
            value => string.IsNullOrWhiteSpace(value) ? $"{label} cannot be empty" : null,
            value => set(value.Trim()),
            Restore(get, set));
    }

    /// <summary>
    /// Free text that may be blank, held as null when it is — the "unset, use the default" fields
    /// (a log directory, an on-connect command).
    /// </summary>
    internal static ScreenField Optional(string label, Func<string?> get, Action<string?> set)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);

        return new ScreenField(
            label,
            () => get() ?? string.Empty,
            _ => null,
            value => set(string.IsNullOrWhiteSpace(value) ? null : value.Trim()),
            Restore(get, set));
    }

    /// <summary>
    /// A .NET regular expression, rejected unless it actually compiles — a trigger or alias whose
    /// pattern doesn't parse would throw on the next line the engine matched, not here.
    /// </summary>
    internal static ScreenField Pattern(string label, Func<string> get, Action<string> set)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);

        return new ScreenField(label, get, ValidatePattern, set, Restore(get, set));
    }

    /// <summary>
    /// Text that may hold newlines (an alias expansion is one command per line), edited on one row
    /// with the breaks written <c>\n</c> — so a multi-line value is still editable without the
    /// screens growing a multi-line editor. A literal backslash round-trips as <c>\\</c>.
    /// </summary>
    internal static ScreenField Lines(string label, Func<string> get, Action<string> set)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);

        return new ScreenField(
            label,
            () => EscapeBreaks(get()),
            value => value.Length == 0 ? $"{label} cannot be empty" : null,
            value => set(ExpandBreaks(value)),
            Restore(get, set));
    }

    /// <summary>A whole number inside an inclusive range — a port, a keepalive interval.</summary>
    internal static ScreenField Integer(string label, Func<int> get, Action<int> set, int min, int max)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);

        return new ScreenField(
            label,
            () => get().ToString(CultureInfo.InvariantCulture),
            value => TryInteger(value, min, max, out _)
                ? null
                : $"{label} must be a whole number {min}-{max}",
            value =>
            {
                TryInteger(value, min, max, out var parsed);
                set(parsed);
            },
            Restore(get, set));
    }

    /// <summary>A fractional number inside an inclusive range — a timer's interval in seconds.</summary>
    internal static ScreenField Number(
        string label, Func<double> get, Action<double> set, double min, double max)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);

        return new ScreenField(
            label,
            () => get().ToString("0.####", CultureInfo.InvariantCulture),
            value => TryNumber(value, min, max, out _)
                ? null
                : $"{label} must be a number {Format(min)}-{Format(max)}",
            value =>
            {
                TryNumber(value, min, max, out var parsed);
                set(parsed);
            },
            Restore(get, set));
    }

    /// <summary>One of a fixed set of names, matched case-insensitively and stored canonically.</summary>
    internal static ScreenField Choice(
        string label, Func<string> get, Action<string> set, IReadOnlyList<string> choices)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(choices);

        return new ScreenField(
            label,
            get,
            value => Canonical(choices, value) is null ? $"{label} must be one of: {string.Join(", ", choices)}" : null,
            value => set(Canonical(choices, value) ?? value),
            Restore(get, set),
            choices);
    }

    /// <summary>An enum value, typed or cycled by name — F9's log format is the canonical case.</summary>
    internal static ScreenField Enumeration<TEnum>(string label, Func<TEnum> get, Action<TEnum> set)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);

        var names = Enum.GetNames<TEnum>();
        return new ScreenField(
            label,
            () => get().ToString() ?? string.Empty,
            value => Canonical(names, value) is null ? $"{label} must be one of: {string.Join(", ", names)}" : null,
            value =>
            {
                if (Enum.TryParse<TEnum>(value.Trim(), ignoreCase: true, out var parsed))
                {
                    set(parsed);
                }
            },
            Restore(get, set),
            names);
    }

    /// <summary>Captures a value of any type and returns the action that writes it back.</summary>
    private static Func<Action> Restore<T>(Func<T> get, Action<T> set) =>
        () =>
        {
            var previous = get();
            return () => set(previous);
        };

    private static string? Canonical(IReadOnlyList<string> choices, string value)
    {
        var trimmed = value.Trim();
        foreach (var choice in choices)
        {
            if (string.Equals(choice, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return choice;
            }
        }

        return null;
    }

    private static bool TryInteger(string value, int min, int max, out int parsed) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
        && parsed >= min && parsed <= max;

    private static bool TryNumber(string value, double min, double max, out double parsed) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
        && parsed >= min && parsed <= max;

    private static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string? ValidatePattern(string value)
    {
        if (value.Length == 0)
        {
            return "pattern cannot be empty";
        }

        try
        {
            _ = new Regex(value);
            return null;
        }
        catch (ArgumentException ex)
        {
            var reason = ex.Message.Split('\n')[0].Trim();
            return "not a valid regex: " + (reason.Length > MaxErrorLength
                ? string.Concat(reason.AsSpan(0, MaxErrorLength - 1), "…")
                : reason);
        }
    }

    /// <summary>Writes a value's line breaks as <c>\n</c> so it fits on one editable row.</summary>
    private static string EscapeBreaks(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    /// <summary>The inverse of <see cref="EscapeBreaks"/>, applied when the buffer is committed.</summary>
    private static string ExpandBreaks(string value)
    {
        var expanded = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[i + 1];
                if (next == 'n')
                {
                    expanded.Append('\n');
                    i++;
                    continue;
                }

                if (next == '\\')
                {
                    expanded.Append('\\');
                    i++;
                    continue;
                }
            }

            expanded.Append(value[i]);
        }

        return expanded.ToString();
    }
}
