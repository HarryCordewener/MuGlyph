using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Commands;

namespace SharpMUTerm.Tui;

/// <summary>What will happen to a keystroke bound to a given descriptor.</summary>
internal enum MacroKeyDelivery
{
    /// <summary>The chord reaches the app's key handler and its macro runs.</summary>
    Fires,

    /// <summary>The terminal or the framework's input parser never produces this chord at all.</summary>
    NeverArrives,

    /// <summary>It arrives, but something else has already claimed it — the app, or the prompt.</summary>
    Taken,
}

/// <summary>
/// What a key descriptor is worth: whether a macro on it can ever run, and — when it cannot — the
/// reason, in the words the F4 screen prints beside the binding.
/// </summary>
/// <param name="Delivery">Which of the three answers this is.</param>
/// <param name="Reason">Why it will not fire, or empty when it will.</param>
internal readonly record struct MacroKeyVerdict(MacroKeyDelivery Delivery, string Reason = "")
{
    /// <summary>Whether pressing this chord actually runs the macro bound to it.</summary>
    internal bool Fires => Delivery == MacroKeyDelivery.Fires;
}

/// <summary>
/// One chord the application claims globally, and what it does with it — the two things F4 needs in
/// order to say <c>Ctrl+Q quits</c> beside a binding that will never run.
/// </summary>
/// <param name="Modifiers">The modifiers the shortcut is registered with (matched exactly).</param>
/// <param name="Key">The key it is registered on.</param>
/// <param name="Does">What it does, as a sentence fragment following the chord's own name.</param>
internal readonly record struct AppShortcut(ConsoleModifiers Modifiers, ConsoleKey Key, string Does);

/// <summary>
/// The bridge between a <see cref="Macro"/>'s descriptor and the keyboard this host actually has.
/// <para>
/// It exists because the two are not the same set, and pretending otherwise is how F4 came to list
/// nine bindings of which none could ever run. Three things narrow what a binding can be. SharpConsoleUI's
/// input parser only decodes some sequences: it produces <c>F1</c>–<c>F12</c> with modifiers (from
/// <c>ESC [ 1;5 P</c> and <c>ESC [ 15;5 ~</c>), Ctrl+letter from the raw control byte, Alt+anything from
/// the ESC prefix, and modified navigation keys — and it produces <see cref="ConsoleKey.NumPad0"/>
/// <em>never</em>, because it sends no DECKPAM and decodes no application-keypad SS3, so a numpad digit
/// arrives as a plain digit or is dropped. Then the app itself takes chords globally
/// (<see cref="AppShortcuts"/>), and a global shortcut runs before any window sees the key. Then the
/// prompt has the rest: an unmodified letter is typing, and a macro that swallowed it would be worse
/// than one that never ran.
/// </para>
/// <para>
/// <see cref="Descriptor"/> — what the dispatcher binds a keystroke to — is defined as
/// <see cref="Capture"/> filtered by <see cref="Verdict"/>, deliberately, so the screen's claim and the
/// dispatcher's behaviour cannot drift: F4 marks exactly the rows the app would refuse to act on.
/// </para>
/// </summary>
internal static class MacroKeys
{
    /// <summary>
    /// Every chord <see cref="SharpMUTermApp"/> registers as a global shortcut. It registers <em>from</em>
    /// this list rather than alongside it, so a key the app takes is a key the keypad screen knows is
    /// taken — there is one list, not two that agree until someone edits one.
    /// </summary>
    internal static IReadOnlyList<AppShortcut> AppShortcuts { get; } = BuildAppShortcuts();

    /// <summary>
    /// The pane number a claimed <see cref="ConsoleKey"/> stands for, or null when the key is not one of
    /// the nine digits ⌥1–⌥9 are registered on. The one place the mapping is written down, so the
    /// registration, the F4 screen and the app's action cannot disagree about which digit is which pane.
    /// </summary>
    internal static int? PaneJumpNumber(ConsoleKey key) =>
        key >= ConsoleKey.D1 && key <= ConsoleKey.D0 + CommandIds.PaneJumpDigits
            ? key - ConsoleKey.D0
            : null;

    /// <remarks>
    /// The fixed claims are a local rather than a field: a static field initialiser runs in declaration
    /// order, so a field read from this method would still be null when the property above calls it.
    /// </remarks>
    private static AppShortcut[] BuildAppShortcuts()
    {
        var claims = new List<AppShortcut>(Fixed());

        // ⌥1–⌥9 — jump to a numbered pane. Generated rather than written out nine times so the digit, the
        // pane number and the sentence F4 prints are one expression; and claimed *here*, in the list the
        // app registers from, because the framework claims Alt+1–9 for its own top-level window selector
        // and that handler is not gated on anything we can switch off (see SharpMUTermApp.JumpToPane).
        // Every one of the nine is claimed, in range or not — an unclaimed digit would fall through to it.
        for (var n = 1; n <= CommandIds.PaneJumpDigits; n++)
        {
            claims.Add(new AppShortcut(ConsoleModifiers.Alt, ConsoleKey.D0 + n, $"goes to pane {n}"));
        }

        return claims.ToArray();
    }

    private static AppShortcut[] Fixed() => new AppShortcut[]
    {
        new(ConsoleModifiers.Control, ConsoleKey.Q, "asks whether to quit"),
        new(ConsoleModifiers.Control, ConsoleKey.N, "picks the next window"),
        new(ConsoleModifiers.Control, ConsoleKey.Tab, "picks the next window"),
        new(ConsoleModifiers.Control, ConsoleKey.W, "closes the window"),
        new(ConsoleModifiers.Control, ConsoleKey.O, "cycles the panes"),
        new(ConsoleModifiers.Control, ConsoleKey.P, "opens the command surface"),
        new(ConsoleModifiers.Control, ConsoleKey.B, "arms the pane prefix"),
        new(ConsoleModifiers.Control, ConsoleKey.F, "freezes the pane"),
        new(ConsoleModifiers.Control, ConsoleKey.R, "searches the command history"),
        // The connection pair. ⌃D is the terminal's own hang-up chord and is spent on the action it
        // idiomatically means; Alt+R spells "Reconnect" one modifier over from the ⌃R this app has
        // already given to the history surface. Both act at once — ⌃Q is the only key in this client
        // that asks anything.
        new(ConsoleModifiers.Control, ConsoleKey.D, "disconnects the focused character"),
        new(ConsoleModifiers.Alt, ConsoleKey.R, "reconnects the focused character"),
        new((ConsoleModifiers)0, ConsoleKey.F2, "opens Triggers"),
        new((ConsoleModifiers)0, ConsoleKey.F3, "opens Aliases"),
        new((ConsoleModifiers)0, ConsoleKey.F4, "opens this screen"),
        new((ConsoleModifiers)0, ConsoleKey.F5, "opens Worlds"),
        new((ConsoleModifiers)0, ConsoleKey.F6, "opens Timers"),
        new((ConsoleModifiers)0, ConsoleKey.F7, "opens Text & ANSI"),
        new((ConsoleModifiers)0, ConsoleKey.F8, "opens Input"),
        new((ConsoleModifiers)0, ConsoleKey.F9, "opens the character's log"),
    };

    /// <summary>The claimed chords by canonical descriptor, built once from <see cref="AppShortcuts"/>.</summary>
    private static readonly Dictionary<string, string> Claimed = BuildClaims();

    /// <summary>
    /// The canonical descriptor for a keystroke, whatever it is worth — what the capture mode writes
    /// into a binding, before the field's validator has a word to say about it. Null when the key has no
    /// name this client can store (a lone modifier, an undecoded sequence arriving as
    /// <see cref="ConsoleKey.NoName"/>).
    /// </summary>
    internal static string? Capture(ConsoleKeyInfo key) =>
        Name(key.Key) is { } name
            ? MacroKey.Describe(
                name,
                key.Modifiers.HasFlag(ConsoleModifiers.Control),
                key.Modifiers.HasFlag(ConsoleModifiers.Alt),
                key.Modifiers.HasFlag(ConsoleModifiers.Shift))
            : null;

    /// <summary>
    /// The chords a new binding may be created on, in the order they are handed out: the function keys
    /// first, then their Ctrl variants. Every entry is one <see cref="Verdict"/> says fires, checked by
    /// test rather than trusted — a list of candidates that drifted from the verdicts would hand out
    /// keys that cannot be reached, which is the exact defect it exists to prevent.
    /// </summary>
    internal static IReadOnlyList<string> Bindable { get; } = BuildBindable();

    private static List<string> BuildBindable()
    {
        var candidates = new List<string>();
        for (var f = 1; f <= 12; f++)
        {
            candidates.Add(MacroKey.Describe($"F{f}"));
        }

        for (var f = 1; f <= 12; f++)
        {
            candidates.Add(MacroKey.Describe($"F{f}", ctrl: true));
        }

        return candidates.FindAll(c => Verdict(c).Fires);
    }

    /// <summary>
    /// The descriptor a keystroke should run a macro on, or null when this keystroke is not the app's to
    /// act on — because nothing could ever be bound to it, or because something else owns it. It is
    /// <see cref="Capture"/> filtered by <see cref="Verdict"/> so that the rows F4 draws as live are
    /// precisely the ones this returns for.
    /// </summary>
    internal static string? Descriptor(ConsoleKeyInfo key) =>
        Capture(key) is { } descriptor && Verdict(descriptor).Fires ? descriptor : null;

    /// <summary>
    /// What a stored descriptor is worth on this host. Every answer is a fact about the parser, the app's
    /// own shortcuts, or the prompt — never a guess — and the reason is short enough to print on the
    /// binding's own row.
    /// </summary>
    internal static MacroKeyVerdict Verdict(string? descriptor)
    {
        if (!MacroKey.TryParse(descriptor, out var parts))
        {
            return Never("not a key this client knows");
        }

        var canonical = MacroKey.Describe(parts.Key, parts.Ctrl, parts.Alt, parts.Shift);
        if (Claimed.TryGetValue(canonical, out var does))
        {
            return new MacroKeyVerdict(MacroKeyDelivery.Taken, $"{canonical} {does}");
        }

        if (parts.Key.Length == 4 && parts.Key.StartsWith("Num", StringComparison.Ordinal)
            && char.IsAsciiDigit(parts.Key[3]))
        {
            // No DECKPAM is ever sent and no application-keypad SS3 is decoded, so the numpad either
            // reports as the main row's digits or is dropped. Either way this descriptor never arrives.
            return Never("no numpad key ever arrives");
        }

        if (parts.Ctrl && parts.Alt)
        {
            return Never("Ctrl+Alt arrives as two keys");
        }

        if (IsFunction(parts.Key))
        {
            return new MacroKeyVerdict(MacroKeyDelivery.Fires);
        }

        if (IsLetter(parts.Key) || IsDigit(parts.Key))
        {
            return Chord(parts);
        }

        if (Navigation.Contains(parts.Key))
        {
            // Ctrl+Shift+←/→ is `kitty_mod+left`/`right` — previous_tab and next_tab — and kitty's
            // dispatcher consumes an action that returns nothing, so those two bytes are never written to
            // the pty at all. This is not the parser's doing (it decodes CSI 1;6 D perfectly, which is what
            // TerminalKeyArrivalTests checks) and no ordering in this app can reach a key the terminal kept:
            // the pane-resize chord was on it, was reported dead, and moved to Alt+Shift because of this.
            // The vertical pair is not listed: kitty_mod+up/down is scroll_line_up/down, which returns
            // "passthrough" while the alternate screen is up, so those do arrive.
            if (parts.Ctrl && parts.Shift && !parts.Alt && parts.Key is "Left" or "Right")
            {
                return Never("the terminal keeps ⌃⇧←/→ for its tabs");
            }

            return parts.Ctrl || parts.Alt || parts.Shift
                ? new MacroKeyVerdict(MacroKeyDelivery.Fires)
                : new MacroKeyVerdict(MacroKeyDelivery.Taken, "unmodified, this belongs to the prompt");
        }

        return Never("only F-keys and Ctrl/Alt chords arrive");
    }

    /// <summary>
    /// What a letter or a digit is worth. Unmodified it is typing; with Ctrl it is a control byte, which
    /// carries no Shift or Alt of its own, which four letters cannot produce at all because the terminal
    /// spells those bytes Tab, Enter and Backspace, and which <em>no</em> digit produces usefully (see
    /// <see cref="DigitBytes"/>); with Alt it is an ESC prefix, except for the one letter the parser has
    /// already spent on its own SS3 introducer. Alt+digit is the one modifier the digit row does deliver,
    /// which is why the pane-jump chords are on it — <see cref="AppShortcuts"/> then reports ⌥1–⌥9 as
    /// taken through <see cref="Claimed"/>, and ⌥0 stays free for a macro.
    /// </summary>
    private static MacroKeyVerdict Chord(MacroKeyParts parts)
    {
        if (!parts.Ctrl && !parts.Alt)
        {
            return new MacroKeyVerdict(MacroKeyDelivery.Taken, "plain keys type into the prompt");
        }

        if (parts.Ctrl)
        {
            if (parts.Shift)
            {
                return Never("a Ctrl chord loses Shift");
            }

            if (DigitBytes.TryGetValue(parts.Key, out var digitSpelling))
            {
                return Never($"the terminal sends {digitSpelling} instead");
            }

            return ControlBytes.TryGetValue(parts.Key, out var spelt)
                ? Never($"the terminal sends {spelt} instead")
                : new MacroKeyVerdict(MacroKeyDelivery.Fires);
        }

        return parts.Key == "O"
            ? Never("Alt+O is the terminal's own prefix")
            : new MacroKeyVerdict(MacroKeyDelivery.Fires);
    }

    /// <summary>
    /// What a terminal actually writes for <c>Ctrl</c>+each digit — the reason the pane-jump chord is
    /// <c>Alt</c> and not <c>Ctrl</c>, which is what was asked for.
    /// <para>
    /// There is no Ctrl+digit encoding to speak of. The digit row has no control bytes of its own beyond
    /// an accident of the ASCII table, so a terminal either sends the bare digit (1, 9, 0 — a chord the
    /// app cannot even tell from typing) or a byte that already belongs to another key. Three of those are
    /// keys this client cannot afford to lose: <c>Ctrl+3</c> is <c>Escape</c>, which is how every overlay
    /// here is closed and half of the Alt+⏎ reassembly; <c>Ctrl+8</c> is <c>Backspace</c>; <c>Ctrl+2</c>
    /// is NUL. Binding any of them would break the plain key rather than add a chord — the same trap
    /// <see cref="ControlBytes"/> records for Ctrl+H/I/J/M.
    /// </para>
    /// <para>
    /// Observed, not remembered: the bytes were read off a real pty by driving each chord at a raw-mode
    /// reader with <c>kitten @ send-key</c> — <c>ESC</c>+digit for every Alt+digit, and this table for
    /// Ctrl. A decode test could not have answered it, for the reason
    /// <c>TerminalKeyArrivalTests</c> spells out: what the parser makes of a byte sequence is a smaller
    /// claim than what the terminal sends.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> DigitBytes = new(StringComparer.Ordinal)
    {
        ["0"] = "a bare 0",
        ["1"] = "a bare 1",
        ["2"] = "NUL",
        ["3"] = "Escape",
        ["4"] = "0x1C",
        ["5"] = "0x1D",
        ["6"] = "0x1E",
        ["7"] = "0x1F",
        ["8"] = "Backspace",
        ["9"] = "a bare 9",
    };

    /// <summary>The letters whose control byte the terminal has already spent on another key.</summary>
    private static readonly Dictionary<string, string> ControlBytes = new(StringComparer.Ordinal)
    {
        ["I"] = "Tab",
        ["M"] = "Enter",
        ["J"] = "Enter",
        ["H"] = "Backspace",
    };

    /// <summary>The keys that are navigation rather than text — bindable, but only as part of a chord.</summary>
    private static readonly HashSet<string> Navigation = new(StringComparer.Ordinal)
    {
        "Up", "Down", "Left", "Right", "Home", "End", "PageUp", "PageDown", "Insert", "Delete",
    };

    private static MacroKeyVerdict Never(string reason) => new(MacroKeyDelivery.NeverArrives, reason);

    private static bool IsFunction(string key) =>
        key.Length is 2 or 3 && key[0] == 'F' && key[1..].All(char.IsAsciiDigit);

    private static bool IsLetter(string key) => key.Length == 1 && char.IsAsciiLetterUpper(key[0]);

    private static bool IsDigit(string key) => key.Length == 1 && char.IsAsciiDigit(key[0]);

    private static Dictionary<string, string> BuildClaims()
    {
        var claims = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var shortcut in AppShortcuts)
        {
            if (Name(shortcut.Key) is { } name)
            {
                claims[MacroKey.Describe(
                    name,
                    shortcut.Modifiers.HasFlag(ConsoleModifiers.Control),
                    shortcut.Modifiers.HasFlag(ConsoleModifiers.Alt),
                    shortcut.Modifiers.HasFlag(ConsoleModifiers.Shift))] = shortcut.Does;
            }
        }

        return claims;
    }

    /// <summary>
    /// The descriptor name of a <see cref="ConsoleKey"/>, or null for one a binding cannot be written
    /// for. It is deliberately wider than what <see cref="Verdict"/> will accept — the numpad keys have
    /// names here even though none of them can arrive, because a configuration already holds those
    /// descriptors and a screen that could not spell them could not tell you they are dead either.
    /// </summary>
    private static string? Name(ConsoleKey key) => key switch
    {
        >= ConsoleKey.F1 and <= ConsoleKey.F24 => "F" + (key - ConsoleKey.F1 + 1),
        >= ConsoleKey.A and <= ConsoleKey.Z => key.ToString(),
        >= ConsoleKey.D0 and <= ConsoleKey.D9 => (key - ConsoleKey.D0).ToString(),
        >= ConsoleKey.NumPad0 and <= ConsoleKey.NumPad9 => "Num" + (key - ConsoleKey.NumPad0),
        ConsoleKey.UpArrow => "Up",
        ConsoleKey.DownArrow => "Down",
        ConsoleKey.LeftArrow => "Left",
        ConsoleKey.RightArrow => "Right",
        ConsoleKey.Home => "Home",
        ConsoleKey.End => "End",
        ConsoleKey.PageUp => "PageUp",
        ConsoleKey.PageDown => "PageDown",
        ConsoleKey.Insert => "Insert",
        ConsoleKey.Delete => "Delete",
        ConsoleKey.Enter => "Enter",
        ConsoleKey.Escape => "Escape",
        ConsoleKey.Tab => "Tab",
        ConsoleKey.Backspace => "Backspace",
        ConsoleKey.Spacebar => "Space",
        _ => null,
    };
}
