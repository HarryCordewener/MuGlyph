using System.Globalization;

namespace SharpMUTerm.Tui;

/// <summary>
/// The one spelling of an unread count, shared by the two surfaces that draw one: the connection rail's
/// per-row badge (<see cref="RailRenderer"/>) and a pane's tab label (<see cref="TabTitles"/>).
/// <para>
/// It is shared because the sidebar and the tab strip are two views of a <em>single</em> number —
/// <c>WorkspaceWindow.Unread</c> — and two formatters would eventually disagree about it. They already
/// did: the rail capped at <see cref="Max"/> while the tab printed the raw integer, so a busy channel
/// read <c>99+</c> in the sidebar and <c>(4127)</c> on its tab, which is two answers to one question.
/// </para>
/// </summary>
internal static class UnreadBadge
{
    /// <summary>The largest count written in full; above it the badge reads <c>99+</c> and stops growing.</summary>
    internal const int Max = 99;

    /// <summary>Cells a badge can occupy, which is the width of <c>99+</c>. See <see cref="RailRenderer"/>.</summary>
    internal const int FieldWidth = 3;

    /// <summary>
    /// The markup colour a count is drawn in — the app accent, on both surfaces.
    /// <para>
    /// One colour on purpose. It is not the focus colour and cannot be mistaken for it: focus is said
    /// entirely in <em>backgrounds</em> drawn from the theme's own chrome family
    /// (<see cref="WorkspacePalette.Focus"/> behind a pane, <see cref="WorkspacePalette.ArmedBand"/>
    /// behind the focused pane's active tab chip), while this is a <em>foreground</em> and the one hue in
    /// the workspace that no plane is ever painted in. So the two cues are orthogonal — a tab can be
    /// focused, unread, both or neither, and each of the four states reads distinctly.
    /// </para>
    /// </summary>
    internal const string Tint = ScreenPalette.Accent;

    /// <summary>A count as it is written, capped at <see cref="Max"/>.</summary>
    internal static string Format(int unread) =>
        unread > Max ? $"{Max}+" : unread.ToString(CultureInfo.InvariantCulture);
}
