using static SharpMUTerm.Tui.MarkupText;
using static SharpMUTerm.Tui.ScreenPalette;

namespace SharpMUTerm.Tui;

/// <summary>
/// The workspace as the ⌃B keymap sees it — everything that decides whether a command on that keymap
/// can do anything at all. Gathered by the app (only the app can see the layout, the active window and
/// the command lines) and read by <see cref="PrefixPanel"/>, so "what does the panel say on a fresh
/// client" is answerable without a terminal.
/// </summary>
/// <param name="PaneCount">How many panes the workspace holds. Zoom and cycle need a second one.</param>
/// <param name="TabCount">
/// How many tabs the focused pane holds. Both splits and both reorders need a second one: a split moves
/// the pane's <em>other</em> tabs across, so with one tab there is nothing to move.
/// </param>
/// <param name="ActiveWindowIsMain">Whether the tab <c>x</c> would close is the session's own window, which stays.</param>
/// <param name="Zoomed">Whether the focused pane is currently zoomed — <c>z</c> is a toggle and says which way.</param>
/// <param name="RailCollapsed">Whether the connection rail is hidden — <c>b</c> is a toggle and says which way.</param>
/// <param name="SecondBarShown">Whether this window has its second command line up — <c>i</c> is a toggle too.</param>
internal readonly record struct PrefixFacts(
    int PaneCount,
    int TabCount,
    bool ActiveWindowIsMain,
    bool Zoomed,
    bool RailCollapsed,
    bool SecondBarShown)
{
    /// <summary>A fresh client: one pane holding one window, the rail out, one command line.</summary>
    internal static PrefixFacts Fresh { get; } = new(1, 1, true, false, false, false);
}

/// <summary>
/// One row of the panel: the keys that run it, what it does, and — when it cannot be run right now —
/// the short reason why.
/// </summary>
/// <param name="Keys">The keys, exactly as the user must press them.</param>
/// <param name="Title">What pressing them does, phrased for someone who has never read the source.</param>
/// <param name="Blocked">
/// Null when the command is live. Otherwise the short reason it is not, which is the same fact the
/// status line spells out at length if the key is pressed anyway (see the <c>…Refusal</c> constants).
/// </param>
internal readonly record struct PrefixEntry(string Keys, string Title, string? Blocked)
{
    /// <summary>Whether pressing these keys would do something.</summary>
    internal bool Available => Blocked is null;
}

/// <summary>
/// The ⌃B keymap as something a first-time user can read: what each key does, and which of them can do
/// anything to the workspace in front of them. Pure — keys in, rendered lines out — for the same reason
/// <see cref="QuitPrompt"/> is, with <see cref="PrefixOverlay"/> left holding nothing but the framework
/// calls.
/// <para>
/// <b>Which-key, not a setting.</b> Arming ⌃B shows the terse <see cref="Strip"/> in the header at once
/// and starts a short timer; if no key has arrived by the time it fires, this panel opens and explains
/// the keymap. That is vim's which-key and emacs' guide-key, and it is deliberately not the
/// modal-versus-strip preference the report offered: an expert never sees the panel because they are
/// already typing, a newcomer is told without having to ask, and neither has to find a setting. The
/// delay is <c>SharpMUTermApp.PrefixPanelDelay</c> and runs on the injected clock.
/// </para>
/// <para>
/// <b>Unavailable commands are shown, dimmed, with their reason.</b> On a fresh client — one pane
/// holding one window — five of the nine commands can do nothing at all, and a panel listing them as
/// live would send the reader to press a key that only refuses. Dimming them instead makes the panel
/// explain the <em>workspace</em> as well as the keymap: "needs a second pane" is why ⌃B looked broken
/// the first time anyone used it.
/// </para>
/// </summary>
internal static class PrefixPanel
{
    // --- the refusals, as the status line spells them ------------------------------------------
    //
    // These live here rather than in RunPrefixCommand so the panel's short note and the sentence the
    // status line prints are one fact written once. The long form is what a user gets when they press
    // the key anyway; the short form is what fits a column beside thirty cells of title.

    /// <summary>Why a split can refuse. Named here because the panel and the status line must agree.</summary>
    internal const string NoSplitRefusal =
        "nothing to split — a split moves this pane's other tabs across, and it has none";

    /// <summary>Why zoom can refuse.</summary>
    internal const string NoZoomRefusal = "nothing to zoom — the workspace has one pane";

    /// <summary>Why cycling can refuse.</summary>
    internal const string NoCycleRefusal = "nowhere to cycle to — the workspace has one pane";

    /// <summary>Why closing can refuse.</summary>
    internal const string NoCloseRefusal = "the main window stays open — ⌃B x closes a spawn or web tab";

    /// <summary>Why reordering can refuse when the pane holds a single tab.</summary>
    internal const string NoReorderRefusal = "nothing to reorder — this pane has one tab";

    /// <summary>Why reordering can refuse when the tab is already at the end it was pushed toward.</summary>
    internal const string TabAtEndRefusal = "the tab is already at that end of the strip";

    // --- the short notes the panel puts beside a dimmed row -------------------------------------

    private const string NeedsSecondTab = "needs a second tab";
    private const string NeedsSecondPane = "needs a second pane";
    private const string MainWindowStays = "the main window stays";

    /// <summary>
    /// The keys the terse strip lists, in the order the panel lists them. Both spellings of the reorder
    /// pair are named: the keymap is the literal <c>&lt;</c> and <c>&gt;</c>, but a bare pair of angle
    /// brackets reads as a direction and the arrows are what a reader reaches for — so they work too
    /// (<c>SharpMUTermApp.PrefixKey</c>) and the strip says so rather than leaving it to be discovered.
    /// </summary>
    internal const string StripKeys = "| - z o 1–9 x b m i < > ← →";

    /// <summary>
    /// How the way out is named, everywhere it is named. The prefix used to have none: Esc worked only by
    /// falling into the "any other key disarms" arm, and no surface said so — while move mode and the pane
    /// drag both end their prompts with <c>Esc cancel</c>.
    /// </summary>
    internal const string ExitHint = "Esc cancels · ⌃B again cancels · any other key disarms";

    /// <summary>The footer under the rows, which is where <see cref="ExitHint"/> is read from.</summary>
    internal static string Footer => ExitHint;

    /// <summary>
    /// What <c>SharpMUTermApp.PrefixKey</c> resolves Escape to, and the case <c>RunPrefixCommand</c>
    /// answers — the cancel, spelled once and named beside the hint that advertises it.
    /// <para>
    /// Cancelling differs from the "any other key disarms" fall-through only in being <em>advertised</em>:
    /// both spend the prefix and neither runs a command, and — the part that matters — neither may leave
    /// the prefix armed, because the key after that would be eaten by a prefix nothing is going to consume.
    /// ⌃B is the other way out and never arrives here at all: a global shortcut runs ahead of any window,
    /// so it is answered by <c>ArmPrefix</c> toggling instead.
    /// </para>
    /// </summary>
    internal const char CancelKey = '\x1b';

    /// <summary>
    /// The keymap against a workspace, in the order it is drawn. Every row here is a key the app really
    /// dispatches (<c>SharpMUTermApp.RunPrefixCommand</c>); nothing is listed that is not.
    /// </summary>
    internal static IReadOnlyList<PrefixEntry> Entries(PrefixFacts facts)
    {
        // A split moves the focused pane's *other* tabs into the new one, so a pane holding a single tab
        // has nothing to move and the split is refused — the same condition both reorders have.
        var needsTab = facts.TabCount > 1 ? null : NeedsSecondTab;
        var needsPane = facts.PaneCount > 1 ? null : NeedsSecondPane;

        return new[]
        {
            new PrefixEntry("|", "split this pane side by side", needsTab),
            new PrefixEntry("-", "split this pane top and bottom", needsTab),
            new PrefixEntry("z", facts.Zoomed ? "unzoom this pane" : "zoom this pane full-window", needsPane),
            new PrefixEntry("o", "go to the next pane", needsPane),

            // The ordinal pane mover, beside the cycle it counts with. It moved here from ⌥1–⌥9 when that
            // was given to *windows*: a pane and a window are different destinations and one key cannot
            // name both. Blocked on the same fact as zoom and cycle — with one pane there is nowhere to
            // go — and listed as the range rather than nine rows, which is how the reorder pair is listed.
            new PrefixEntry("1–9", "go to that numbered pane", needsPane),
            new PrefixEntry("x", "close this tab", facts.ActiveWindowIsMain ? MainWindowStays : null),
            new PrefixEntry("b", facts.RailCollapsed ? "show the connection rail" : "hide the connection rail", null),
            new PrefixEntry("m", "move this window to another pane", null),
            new PrefixEntry(
                "i",
                facts.SecondBarShown ? "hide the second command line" : "show a second command line",
                null),
            new PrefixEntry("< >  ← →", "reorder this tab", needsTab),
        };
    }

    /// <summary>
    /// The whole panel as markup lines: one row per command, dimmed with its reason where it cannot run,
    /// then the footer naming the way out.
    /// </summary>
    internal static List<string> Render(PrefixFacts facts)
    {
        var entries = Entries(facts);
        var keyColumn = entries.Max(e => e.Keys.Length);
        var titleColumn = entries.Max(e => e.Title.Length);

        var lines = new List<string>(entries.Count + 2);
        foreach (var entry in entries)
        {
            // The leading space is not decoration: the first key on the list is `|`, and drawn hard against
            // the window's own `│` border the two read as one glyph.
            var keys = " " + entry.Keys.PadRight(keyColumn);
            var title = entry.Blocked is null ? entry.Title : entry.Title.PadRight(titleColumn);
            // A blocked row is one flat mid-grey — key included. The key must stay *legible*: the reader is
            // being told which keystroke is unavailable, and a key drawn in the hairline tone (which is what
            // this did first) disappears into the panel it is on, leaving a reason attached to nothing.
            lines.Add(entry.Blocked is null
                ? $"[bold {Accent}]{Escape(keys)}[/]  [{Value}]{Escape(title)}[/]"
                : $"[{Label}]{Escape(keys)}[/]  [{Label}]{Escape(title)}[/]  [{Label}]— {Escape(entry.Blocked)}[/]");
        }

        lines.Add(string.Empty);
        lines.Add($" [{Label}]{Escape(Footer)}[/]");
        return lines;
    }

    /// <summary>The widest visible row, for the host to size its window to.</summary>
    internal static int MaxWidth(IReadOnlyList<string> lines) =>
        lines.Count == 0 ? 0 : lines.Max(VisibleLength);

    /// <summary>
    /// The terse header strip, in the longest spelling that fits <paramref name="available"/> cells.
    /// <para>
    /// It is width-aware because the header is one row and the framework <em>wraps</em> an overlong one —
    /// and a header that grows a row takes one off the workspace. The armed strip already ran the eighty-
    /// column layout to within four cells of the edge before this change added anything to it, so the
    /// fallbacks are not hypothetical. Every form names the way out, because the honesty rule applies to
    /// the short spellings too; the shortest gives up the keys rather than the exit, since a terminal that
    /// narrow is exactly the one where the panel does the explaining a moment later.
    /// </para>
    /// </summary>
    internal static string Strip(int available)
    {
        foreach (var (plain, markup) in Forms)
        {
            if (plain.Length <= available)
            {
                return markup;
            }
        }

        return Forms[^1].Markup;
    }

    /// <summary>The strip's spellings, widest first, each as the plain text to measure and the markup to emit.</summary>
    private static readonly (string Plain, string Markup)[] Forms =
    {
        ($"⌃B — awaiting  {StripKeys}  ·  Esc cancels",
            $"[#e5c07b]⌃B — awaiting[/]  [dim]{StripKeys}  ·  Esc cancels[/]"),
        ($"⌃B  {StripKeys}  Esc",
            $"[#e5c07b]⌃B[/]  [dim]{StripKeys}  Esc[/]"),
        ("⌃B — Esc cancels", "[#e5c07b]⌃B[/]  [dim]Esc cancels[/]"),
    };
}
