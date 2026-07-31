using System.Globalization;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Telnet.Mssp;
using static SharpMUTerm.Tui.MarkupText;
using static SharpMUTerm.Tui.ScreenPalette;

namespace SharpMUTerm.Tui;

/// <summary>
/// The read-only MSSP report for one world — what the server said about itself, and when it said it.
/// Reached from F5 with <c>i</c> on the selected world.
/// <para>
/// <b>Read-only is the shape, not an omission.</b> Every other settings screen exists to change
/// something, and this project's affordance rule is that a field well means "the keyboard can change
/// this here" and its absence means it cannot (<see cref="ScreenChrome.ReadOnly"/>). A whole screen of
/// well-less rows is therefore exactly right, and it has to be <em>deliberately</em> well-less so it does
/// not read as a form whose wells failed to render. Nothing here calls <see cref="ScreenChrome.Field"/>.
/// </para>
/// <para>
/// <b>Three states, and the two empty ones must not look the same.</b> A world we have never connected
/// to, a server that answered and publishes no MSSP, and a report. Conflating the last two is the easy
/// mistake and it is the one that makes a client look broken: on a MUSH, "this server publishes no MSSP"
/// is the ordinary answer, and saying so — with what we do know from the world's own configuration
/// beside it — is the same information as "no data" and the opposite impression.
/// </para>
/// <para>
/// <b>Every value here came off the wire from a stranger.</b> Three things follow, and all three
/// happen in <c>Row</c> in this order: control characters are replaced (a raw newline in a value would
/// otherwise end the row and shift everything below it), the raw text is truncated <em>before</em>
/// escaping (truncating escaped markup can split a <c>[[</c> pair into an unbalanced tag), and only
/// then is it escaped. Column widths are functions of the <em>terminal</em> and never of the data,
/// because this repository has twice paid for chrome whose width was a function of something arriving
/// from the wire — <c>RailRenderer.UnsentFieldWidth</c>, and the status row's scrollback distance that
/// wrapped every pane's height at 99 → 100.
/// </para>
/// </summary>
internal static class MsspScreenRenderer
{
    /// <summary>The screen's title, and what the ⌃P entry and the snapshot view are named after.</summary>
    internal const string Title = "Server information";

    /// <summary>The snapshot view name (<c>--view mssp</c>).</summary>
    internal const string View = "mssp";

    /// <summary>
    /// How wide the variable-name column runs. Official MSSP names top out at <c>XTERM 256 COLORS</c>
    /// (17) and <c>HIRING BUILDERS</c> (15); an invented one can be any length at all, so the column is
    /// a constant and a longer name is elided into it rather than being allowed to push the values
    /// right. See the type summary for why that is not fussiness.
    /// </summary>
    internal const int NameWidth = 20;

    /// <summary>
    /// The most of one value that is ever drawn. A server may legitimately send a long <c>WEBSITE</c> or
    /// a wordy <c>DESCRIPTION</c>; it may also send half a megabyte. <see cref="MsspCache.MaxValueLength"/>
    /// already bounds what is stored — this bounds what one row spends, which is the layout half of the
    /// same rule.
    /// </summary>
    internal const int ValueWidth = 58;

    /// <summary>The fewest cells a value is still worth drawing in, on a terminal too narrow for more.</summary>
    private const int MinValueWidth = 16;

    /// <summary>What a row spends before the value: the mark column, the name column, and their gaps.</summary>
    private const int RowChrome = 1 + 1 + 1 + NameWidth + 2;

    /// <summary>
    /// How much of a value this frame can afford — <see cref="ValueWidth"/> where there is room for it,
    /// and what is left of the terminal where there is not.
    /// <para>
    /// <b>A function of the terminal, never of the data.</b> That is the whole discipline: this
    /// repository has twice shipped chrome whose width was a function of something arriving from the
    /// wire, and both times the symptom was a row growing past its box and taking a row off everything
    /// below it. A value at the full 58 cells needs 83, so at 80 columns the constant alone would have
    /// overrun by three — visible in a rendered frame and in nothing else.
    /// </para>
    /// </summary>
    private static int ValueCells(int width) =>
        width <= 0 ? ValueWidth : Math.Clamp(width - RowChrome - 1, MinValueWidth, ValueWidth);

    /// <summary>
    /// How many values of one variable are listed before the rest are summarised. Multi-valued variables
    /// are real and are the reason the model is a name → <em>list</em> map at all (<c>PORT</c>,
    /// <c>REFERRAL</c>, <c>CODEBASE</c>), so they are drawn as the list they are — one value per row,
    /// the name printed once — rather than as their first or last value. This caps the rows one variable
    /// can spend.
    /// </summary>
    internal const int MaxValueRows = 8;

    /// <summary>What a row shows where the server said nothing at all.</summary>
    internal const string Unreported = "—";

    /// <summary>What a numeric world variable of <c>-1</c> reads as: the specification's "not available".</summary>
    internal const string Unavailable = "unknown";

    /// <summary>The words the never-connected state is put in.</summary>
    internal const string NeverConnected = "No server information yet — connect once and this fills in.";

    /// <summary>
    /// The words the connected-and-silent state is put in. It says the absence is normal because it is:
    /// MSSP is optional, most MUSHes do not implement it, and a client that reported the ordinary case
    /// as a failure would be teaching people to distrust the screen.
    /// </summary>
    internal const string NoMssp = "This server does not publish MSSP. It is optional, and most MUSHes do not.";

    /// <summary>
    /// The first-class rows, in the order somebody browsing a world list wants them. Everything not on
    /// this list is still shown — under <see cref="EverythingElse"/> — because a protocol whose entire
    /// purpose is servers describing themselves is a protocol whose unofficial half is where the
    /// interesting things are.
    /// </summary>
    private static readonly (string Label, string Variable)[] Headline =
    [
        ("name", MsspVariables.Name),
        ("players", MsspVariables.Players),
        ("uptime", MsspVariables.Uptime),
        ("codebase", MsspVariables.Codebase),
        ("family", MsspVariables.Family),
        ("hostname", MsspVariables.Hostname),
        ("port", MsspVariables.Port),
        ("ssl", MsspVariables.Ssl),
        ("charset", MsspVariables.Charset),
        ("contact", MsspVariables.Contact),
        ("website", MsspVariables.Website),
        ("genre", MsspVariables.Genre),
        ("status", MsspVariables.Status),
        ("minimum age", MsspVariables.MinimumAge),
    ];

    /// <summary>The heading over the variables that did not earn a headline row.</summary>
    internal const string EverythingElse = "EVERYTHING ELSE";

    /// <summary>The heading over the headline rows.</summary>
    internal const string SummaryHeading = "SERVER";

    /// <summary>
    /// How an unofficial variable is marked. Official and unofficial must both be visible and must be
    /// told apart: a name the specification defines is a claim a crawler and a client read the same way,
    /// and a name somebody's codebase invented is not — and the reader cannot tell which is which from
    /// the name alone (<c>DISCORD</c> is official as of 2.7.0; <c>PUEBLO</c>, which looks every bit as
    /// standard, is not).
    /// </summary>
    internal const string UnofficialMark = "·";

    /// <summary>The legend that says what <see cref="UnofficialMark"/> means, so the mark is readable.</summary>
    internal const string UnofficialLegend = "not in the MSSP specification";

    /// <summary>
    /// The screen's navigable shape: one stop per drawn body row, so ↑↓ scroll a report longer than the
    /// screen through <see cref="ScreenChrome.Window"/>.
    /// <para>
    /// There is nothing to edit, nothing to toggle and nothing to remove, and the shape says so: the
    /// header offers no <c>⏎ edit</c>, no <c>Space toggle</c> and no <c>Del remove</c>, because every one
    /// of those hints is derived from this model rather than written by the screen.
    /// </para>
    /// </summary>
    internal static ScreenModel Model(
        WorldDefinition? world, MsspObservation? observation, DateTimeOffset now, int width = 0) =>
        new(ScreenModel.Stops(Body(world, observation, now, width).Count));

    /// <summary>
    /// The screen's header band: its title, and the one key that leaves it. It does not go through
    /// <see cref="ScreenChrome.Hints"/> because that composes a settings screen's contract —
    /// <c>F5/Esc close</c> — and this screen's Esc does something else: it goes <em>back</em> to the
    /// screen that opened it, with its selection intact. A header offering "close" would name the right
    /// key for the wrong outcome.
    /// </summary>
    internal static string HeaderLine(int width)
    {
        var hints = $"[{Label}]↑↓ scroll · [/][{Accent}]Esc[/][{Label}] back [/]";
        return SpreadLR($" [bold {Value}] {Escape(Title)}[/]", hints, width);
    }

    /// <summary>The screen's action bar: where the cursor is in the report, and the key that leaves.</summary>
    internal static string FooterLine(
        WorldDefinition? world, MsspObservation? observation, ScreenFocus? focus, int width, DateTimeOffset now)
    {
        var rows = Body(world, observation, now, width).Count;
        var context = ScreenChrome.Context(
            world is null ? null : Escape(world.Name),
            observation is null ? null : Escape(observation.Endpoint),
            focus is { } cursor && cursor.Index >= 0 && rows > 0
                ? ScreenChrome.Position("row", cursor.Index, rows)
                : null);

        return SpreadLR(
            " " + context,
            $"[{Label}] [[Esc]] Back [/]",
            width);
    }

    /// <summary>
    /// The whole report as markup rows, with the cursor band on the focused one and the block windowed
    /// to <paramref name="height"/>. <see cref="Body"/> is the same rows without either, which is what
    /// <see cref="Model"/> counts — so the number of cursor stops and the number of drawn rows are one
    /// number by construction rather than by two functions agreeing.
    /// </summary>
    internal static List<string> Render(
        WorldDefinition? world,
        MsspObservation? observation,
        DateTimeOffset now,
        ScreenFocus? focus = null,
        int height = 0,
        int width = 0)
    {
        var cursor = focus ?? ScreenFocus.None;
        var body = Body(world, observation, now, width);
        for (var i = 0; i < body.Count; i++)
        {
            // The bar spans the terminal, not the columns: this screen is one pane filling the window,
            // so a bar that stopped where the value column stops would read as a second, invisible
            // column edge. On F5 the same call pads to the pane's width, which is the same rule.
            body[i] = ScreenChrome.Cursor(
                body[i], cursor.IsOn(0, i), width > 0 ? width : RowChrome + ValueWidth);
        }

        return ScreenChrome.Window(body, height);
    }

    /// <summary>
    /// The report's rows, unfocused and unwindowed. Deterministic in its arguments, because
    /// <see cref="Model"/> counts these rows and <see cref="Render"/> draws them: two functions that
    /// disagreed about how many there are would give the screen a cursor stop nobody ever drew, which is
    /// the failure <see cref="ScreenChrome.Window"/> exists one level up to stop.
    /// </summary>
    internal static List<string> Body(
        WorldDefinition? world, MsspObservation? observation, DateTimeOffset now, int width = 0)
    {
        var cells = ValueCells(width);
        var rows = new List<string>();
        AddConfigured(rows, world, cells);

        if (observation is null)
        {
            rows.Add(string.Empty);
            rows.Add($"  [{Muted}]{Escape(NeverConnected)}[/]");
            return rows;
        }

        if (observation is { Report: null } or { ObservedAt: null })
        {
            rows.Add(string.Empty);
            rows.Add($"  [{Muted}]{Escape(NoMssp)}[/]");
            rows.Add(string.Empty);
            rows.Add(Row("last seen", Since(observation.ConnectedAt, now), cells));
            return rows;
        }

        var report = observation.Report!;

        // Built once. MsspData.UnofficialNames allocates and filters on every read, and the mark column
        // asks per row — a hundred-variable report would have walked it a hundred times.
        var unofficial = new HashSet<string>(report.UnofficialNames, StringComparer.Ordinal);
        rows.Add(string.Empty);
        rows.Add(Row("captured", Since(observation.ObservedAt!.Value, now), cells));
        rows.Add(string.Empty);
        rows.Add($"  [{Label}]{SummaryHeading}[/]");

        var drawn = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (label, variable) in Headline)
        {
            drawn.Add(variable);
            AddVariable(rows, label, report[variable], variable, report, unofficial, now, cells);
        }

        var remaining = report.Keys.Where(name => !drawn.Contains(name)).ToList();
        rows.Add(string.Empty);
        rows.Add($"  [{Label}]{EverythingElse}[/]");
        if (remaining.Count == 0)
        {
            rows.Add($"  [{Muted}]nothing else was sent[/]");
            return rows;
        }

        foreach (var name in remaining)
        {
            AddVariable(rows, name, report[name], name, report, unofficial, now, cells);
        }

        rows.Add(string.Empty);
        rows.Add($"  [{Muted}]{UnofficialMark}  {Escape(UnofficialLegend)}[/]");
        return rows;
    }

    /// <summary>
    /// What the client knows without asking anybody: the world's own name, host, port and whether the
    /// connection is encrypted. It heads every state, including both empty ones — which is the point.
    /// A screen that had nothing at all to show for a world it has never reached would be a screen you
    /// stop opening; one that shows the configuration it does hold answers half the question.
    /// </summary>
    private static void AddConfigured(List<string> rows, WorldDefinition? world, int cells)
    {
        if (world is null)
        {
            rows.Add($"  [{Muted}]no world selected[/]");
            return;
        }

        rows.Add(Row("world", world.Name, cells));
        rows.Add(Row(
            "address",
            $"{world.Host}:{world.Port.ToString(CultureInfo.InvariantCulture)}",
            cells));
        rows.Add(Row("transport", world.UseTls ? "TLS" : "plain", cells));
    }

    /// <summary>
    /// One variable, as one row per value. The name is printed on the first row only, so a three-port
    /// server reads as one variable with three values rather than as three variables — and the values
    /// are drawn least-to-most-relevant, in wire order, which is the order the specification gives them
    /// meaning in ("the last reported value should be used as the default value").
    /// </summary>
    private static void AddVariable(
        List<string> rows,
        string label,
        IReadOnlyList<string> values,
        string variable,
        MsspData report,
        IReadOnlySet<string> unofficial,
        DateTimeOffset now,
        int cells)
    {
        var mark = unofficial.Contains(variable) ? UnofficialMark : " ";

        if (values.Count == 0)
        {
            // Two different absences, and the row says which. A variable the server sent with no value
            // is a fact about the server; one it never mentioned is a fact about the report.
            rows.Add(Row(label, report.ContainsKey(variable) ? Unavailable : Unreported, cells, mark));
            return;
        }

        var shown = Math.Min(values.Count, MaxValueRows);
        for (var i = 0; i < shown; i++)
        {
            rows.Add(Row(
                i == 0 ? label : string.Empty,
                Reading(variable, values[i], now),
                cells,
                i == 0 ? mark : " "));
        }

        if (values.Count > shown)
        {
            rows.Add(Row(
                string.Empty,
                $"… {(values.Count - shown).ToString(CultureInfo.InvariantCulture)} more",
                cells));
        }
    }

    /// <summary>
    /// How one value reads. Almost all of them read as themselves; the two exceptions are the ones where
    /// the raw string is actively misleading — <c>UPTIME</c> is a Unix timestamp, which is a number
    /// nobody can read as a duration, and <c>-1</c> is the specification's marker for "this server
    /// cannot count that" rather than a count of minus one.
    /// </summary>
    private static string Reading(string variable, string value, DateTimeOffset now)
    {
        if (string.Equals(variable, MsspVariables.Uptime, StringComparison.Ordinal)
            && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)
            && unix > 0)
        {
            var booted = DateTimeOffset.FromUnixTimeSeconds(unix);
            return $"{Duration(now - booted)} (since {booted.UtcDateTime:yyyy-MM-dd HH:mm} UTC)";
        }

        return string.Equals(value, "-1", StringComparison.Ordinal) ? Unavailable : value;
    }

    /// <summary>
    /// A <c>label   value</c> row. Both halves are sanitised, truncated and escaped, and the whole thing
    /// carries a one-cell mark column — reserved, blank when the variable is official, so a report of
    /// unofficial variables and one of official variables put their values in the same column.
    /// <para>
    /// <b>The label is padded before it is escaped, and it is a label off the wire.</b> Under
    /// EVERYTHING ELSE the label <em>is</em> the variable name the server sent, so it is exactly as
    /// hostile as a value: <c>Escape</c> doubles every bracket, so padding the escaped string pads a
    /// name containing <c>[</c> to fewer <em>visible</em> cells than <see cref="NameWidth"/> and the
    /// value column steps left on that row alone. Sanitising it matters for the same reason — MSSP
    /// says names are upper-case letters and spaces, and a server is not obliged to be truthful.
    /// </para>
    /// </summary>
    private static string Row(string label, string value, int cells, string mark = " ") =>
        $" [{Muted}]{mark}[/] [{Label}]{Escape(Fit(Sanitize(label), NameWidth).PadRight(NameWidth))}[/]  "
        + ScreenChrome.ReadOnly(Fit(Sanitize(value), cells));

    /// <summary>
    /// Replaces every control character with a space. This is the first thing done to any value and it
    /// is not cosmetic: a markup block is a list of rows, so a raw <c>\n</c> inside one value would end
    /// that row early and push a fragment of a stranger's text onto a line of its own, below the row it
    /// belongs to and outside the column it was measured for. An <c>ESC</c> would be worse — the frame
    /// is ANSI, and the compositor is not the only thing that reads it.
    /// </summary>
    private static string Sanitize(string value)
    {
        if (!value.Any(char.IsControl))
        {
            return value;
        }

        return string.Create(value.Length, value, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? ' ' : source[i];
            }
        });
    }

    /// <summary>
    /// Truncates raw text to <paramref name="width"/> cells, ellipsis included in the count. It runs
    /// <em>before</em> <see cref="MarkupText.Escape"/> at every call site: escaping doubles every bracket,
    /// so truncating escaped markup can cut a <c>[[</c> in half and leave an unbalanced tag the parser
    /// then eats the rest of the row with.
    /// </summary>
    private static string Fit(string text, int width) =>
        text.Length <= width ? text : text[..Math.Max(1, width - 1)] + "…";

    /// <summary>
    /// How long ago something was, in words, with the exact instant beside it. Both halves earn their
    /// place: "3 days ago" is what tells a reader the player count in front of them is not current, and
    /// the timestamp is what lets them decide whether it matters. A screen that presented a week-old
    /// snapshot with no date at all would be reporting stale data as fact.
    /// </summary>
    private static string Since(DateTimeOffset at, DateTimeOffset now)
    {
        var ago = now - at;
        var words = ago < TimeSpan.Zero ? "just now" : $"{Duration(ago)} ago";
        return $"{words}  ({at.UtcDateTime:yyyy-MM-dd HH:mm} UTC)";
    }

    /// <summary>A coarse duration — the largest unit that is not zero, which is all any of this needs.</summary>
    private static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalDays >= 1 ? Plural((int)span.TotalDays, "day")
            : span.TotalHours >= 1 ? Plural((int)span.TotalHours, "hour")
            : span.TotalMinutes >= 1 ? Plural((int)span.TotalMinutes, "minute")
            : "moments";
    }

    private static string Plural(int count, string noun) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {noun}{(count == 1 ? string.Empty : "s")}";
}
