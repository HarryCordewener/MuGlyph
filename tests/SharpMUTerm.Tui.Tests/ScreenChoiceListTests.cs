using System.Text.RegularExpressions;
using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The candidate list <see cref="ScreenChrome.Choices"/> draws beneath an open field — shared chrome,
/// so it is asserted once here and reached through the screens that use it at the bottom of the file.
/// <para>
/// What it has to get right: typing narrows the list rather than leaving it a menu you have to walk;
/// a buffer that matches nothing reads as legal on a field that takes new values and merely as
/// unmatched on one that doesn't; a list longer than the pane says how much of itself it is showing;
/// and the whole thing overlays the pane rather than growing it, because two of the screens size a
/// grid row from the block's own line count.
/// </para>
/// </summary>
public class ScreenChoiceListTests
{
    private static readonly string[] Routes = { "main", "Chat", "pages", "trade" };

    private static readonly Regex Tags = new(@"\[[^\[\]]*\]");

    /// <summary>A column of plain rows with one open field's caret on the row at <paramref name="at"/>.</summary>
    private static List<string> Column(int rows, int at, ScreenFieldEdit edit)
    {
        var lines = Enumerable.Range(0, rows).Select(i => $"[dim]row {i}[/]").ToList();
        lines[at] = "  " + ScreenChrome.Field("value", edit);
        return lines;
    }

    private static bool IsMenu(string line) =>
        line.Contains($"[on {ScreenPalette.MenuBg}]", StringComparison.Ordinal)
        || line.Contains($"[on {ScreenPalette.MenuSelectedBg}]", StringComparison.Ordinal);

    private static bool IsCaption(string line) =>
        IsMenu(line) && (line.Contains('▾') || line.Contains('▴'));

    private static string Caption(IEnumerable<string> lines) =>
        Tags.Replace(lines.Single(IsCaption), string.Empty).Trim();

    /// <summary>The drawn candidates, in order, without their markup — captions and shadow excluded.</summary>
    private static List<string> Entries(IEnumerable<string> lines) => lines
        .Where(l => IsMenu(l) && !IsCaption(l))
        .Select(l => Tags.Replace(l, string.Empty).Replace("▸", string.Empty, StringComparison.Ordinal).Trim())
        .ToList();

    /// <summary>The one candidate drawn as the value the buffer names, or null when none is.</summary>
    private static string? Marked(IEnumerable<string> lines) => lines
        .Where(l => l.Contains($"[on {ScreenPalette.MenuSelectedBg}]", StringComparison.Ordinal))
        .Select(l => Tags.Replace(l, string.Empty).Replace("▸", string.Empty, StringComparison.Ordinal).Trim())
        .SingleOrDefault();

    [Test]
    public async Task AFieldWithNothingToOfferDrawsNoListAtAll()
    {
        var lines = Column(12, 3, new ScreenFieldEdit(0, "aardmud.org", 11, null));
        var drawn = ScreenChrome.Choices(lines, new ScreenFieldEdit(0, "aardmud.org", 11, null), 56);

        await Assert.That(drawn.Any(IsMenu)).IsFalse();
    }

    /// <summary>
    /// A block that isn't drawing the open edit has no caret in it, so it must come back untouched —
    /// this is what lets every column call the chrome unconditionally without knowing whose field is
    /// open. F5 draws three of these blocks side by side.
    /// </summary>
    [Test]
    public async Task AColumnThatIsNotDrawingTheOpenFieldIsLeftAlone()
    {
        var elsewhere = Enumerable.Range(0, 8).Select(i => $"[dim]row {i}[/]").ToList();
        var drawn = ScreenChrome.Choices(
            elsewhere, new ScreenFieldEdit(0, "Chat", 4, null, Choices: Routes), 56);

        await Assert.That(drawn.Any(IsMenu)).IsFalse();
        await Assert.That(drawn.Count).IsEqualTo(8);
    }

    /// <summary>
    /// A field opens on its committed value, so a plain filter would collapse the list to that one
    /// entry the instant it was drawn — the dropdown would never show the alternatives it exists for.
    /// A buffer that <em>names</em> a choice is therefore a selection: the whole list stays, marked.
    /// </summary>
    [Test]
    public async Task ABufferThatNamesAChoiceKeepsTheWholeListAndMarksIt()
    {
        var edit = new ScreenFieldEdit(0, "Chat", 4, null, Choices: Routes);
        var drawn = ScreenChrome.Choices(Column(14, 2, edit), edit, 56);

        await Assert.That(Entries(drawn)).IsEquivalentTo(new[] { "main", "Chat", "pages", "trade" });
        await Assert.That(Marked(drawn)).IsEqualTo("Chat");
        await Assert.That(Caption(drawn)).Contains(ScreenChrome.OpenChoicesCaption);
    }

    [Test]
    public async Task TypingNarrowsTheListToWhatItMatches()
    {
        var edit = new ScreenFieldEdit(0, "pa", 2, null, Choices: Routes);
        var drawn = ScreenChrome.Choices(Column(14, 2, edit), edit, 56);

        await Assert.That(Entries(drawn)).IsEquivalentTo(new[] { "pages" });

        // Nothing is marked: "pa" is not the name of anything, so no entry is the value yet.
        await Assert.That(Marked(drawn)).IsNull();
        await Assert.That(Caption(drawn)).Contains("1 of 4");
    }

    /// <summary>Substring, not prefix — a colour is as often remembered by its middle as its start.</summary>
    [Test]
    public async Task TheFilterMatchesAnywhereInAName()
    {
        await Assert.That(ScreenField.Matching(ScreenColours.Palette, "gre"))
            .IsEquivalentTo(new[] { "green", "grey" });
        await Assert.That(ScreenField.Matching(Routes, "ra")).IsEquivalentTo(new[] { "trade" });
        await Assert.That(ScreenField.Matching(Routes, string.Empty)).IsEquivalentTo(Routes);
    }

    /// <summary>
    /// The empty filter is the whole reason the route field stopped being a radio group: a name that
    /// matches nothing is exactly how the next spawn window is created, so an empty list must not read
    /// as a refusal. It says so in words, and — decisively — does not raise its voice: the
    /// <see cref="ScreenPalette.Warn"/> ink is reserved for a value that has actually been refused.
    /// </summary>
    [Test]
    public async Task AnOpenListThatMatchesNothingSaysTheValueIsAllowed()
    {
        var edit = new ScreenFieldEdit(0, "combat", 6, null, Choices: Routes);
        var drawn = ScreenChrome.Choices(Column(14, 2, edit), edit, 56);

        await Assert.That(Entries(drawn)).IsEmpty();
        await Assert.That(Caption(drawn)).Contains(ScreenChrome.NoMatchOpen);
        await Assert.That(drawn.Any(l => l.Contains(ScreenPalette.Warn, StringComparison.Ordinal))).IsFalse();
    }

    /// <summary>
    /// The same state on a closed field states the fact and stops: the value really will be refused,
    /// but by the field's own validator at ⏎, and promising a new value would be a lie.
    /// </summary>
    [Test]
    public async Task AClosedListThatMatchesNothingDoesNotPromiseANewValue()
    {
        var formats = new[] { "None", "Plain", "Html", "Both" };
        var edit = new ScreenFieldEdit(0, "Verbose", 7, null, Choices: formats, ClosedChoices: true);
        var drawn = ScreenChrome.Choices(Column(14, 2, edit), edit, 56);

        await Assert.That(Entries(drawn)).IsEmpty();
        await Assert.That(Caption(drawn)).Contains(ScreenChrome.NoMatchClosed);
        await Assert.That(Caption(drawn)).DoesNotContain(ScreenChrome.NoMatchOpen);
        await Assert.That(drawn.Any(l => l.Contains(ScreenPalette.Warn, StringComparison.Ordinal))).IsFalse();
    }

    /// <summary>
    /// The two lists must not look alike. One says "here are the values in use, type another if you
    /// want"; the other says "these four are all there are". Drawn identically, the first would imply a
    /// closed set and the second would invite a value it is about to refuse.
    /// </summary>
    [Test]
    public async Task OpenAndClosedListsAreCaptionedApart()
    {
        var open = new ScreenFieldEdit(0, "Chat", 4, null, Choices: Routes);
        var closed = new ScreenFieldEdit(0, "Chat", 4, null, Choices: Routes, ClosedChoices: true);

        var openCaption = Caption(ScreenChrome.Choices(Column(14, 2, open), open, 56));
        var closedCaption = Caption(ScreenChrome.Choices(Column(14, 2, closed), closed, 56));

        await Assert.That(openCaption).Contains(ScreenChrome.OpenChoicesCaption);
        await Assert.That(openCaption).DoesNotContain(ScreenChrome.ClosedChoicesCaption);
        await Assert.That(closedCaption).Contains(ScreenChrome.ClosedChoicesCaption);
        await Assert.That(closedCaption).DoesNotContain(ScreenChrome.OpenChoicesCaption);
    }

    /// <summary>
    /// Seventeen colour names is more rows than F2's editor pane has to spare, so the list is capped —
    /// and says what it is capped to, because a list silently showing a third of itself would be worse
    /// than none.
    /// </summary>
    [Test]
    public async Task ALongListIsCappedAndSaysHowMuchOfItselfItIsShowing()
    {
        var edit = new ScreenFieldEdit(0, "none", 4, null, Choices: ScreenColours.Palette);
        var drawn = ScreenChrome.Choices(Column(24, 2, edit), edit, 56);

        await Assert.That(ScreenColours.Palette.Count).IsGreaterThan(ScreenChrome.MaxChoiceRows);
        await Assert.That(Entries(drawn).Count).IsEqualTo(ScreenChrome.MaxChoiceRows);
        await Assert.That(Caption(drawn)).Contains($"{ScreenChrome.MaxChoiceRows} of {ScreenColours.Palette.Count}");
    }

    /// <summary>
    /// A cap with no window would make the last eleven colours unreachable to the eye: ↑↓ would move a
    /// mark that had scrolled off the block. The window follows the mark instead.
    /// </summary>
    [Test]
    public async Task TheCapWindowsAroundTheMarkedEntry()
    {
        var last = ScreenColours.Palette[^1];
        var edit = new ScreenFieldEdit(0, last, last.Length, null, Choices: ScreenColours.Palette);
        var drawn = ScreenChrome.Choices(Column(24, 2, edit), edit, 56);

        await Assert.That(Entries(drawn)).Contains(last);
        await Assert.That(Marked(drawn)).IsEqualTo(last);
        await Assert.That(Entries(drawn)).DoesNotContain(ScreenColours.Palette[0]);
    }

    /// <summary>
    /// The overlay rule, which is the reason the shared chrome could be dropped into six renderers at
    /// all: a block's line count is the same open and closed. F5 sizes a grid row from
    /// <c>FormColumn</c>'s count, so a list that pushed rows down would resize the screen on ⏎.
    /// </summary>
    [Test]
    public async Task TheListOverlaysThePaneAndNeverChangesItsHeight()
    {
        var edit = new ScreenFieldEdit(0, "Chat", 4, null, Choices: Routes);
        var before = Column(14, 2, edit);
        var after = ScreenChrome.Choices(Column(14, 2, edit), edit, 56);

        await Assert.That(after.Count).IsEqualTo(before.Count);
        await Assert.That(after.Count(IsMenu)).IsGreaterThan(0);

        // The rows past the block are untouched — it covers its neighbours, it doesn't shuffle them.
        await Assert.That(after[^1]).IsEqualTo(before[^1]);
    }

    [Test]
    public async Task AListWithNoRoomBelowItIsDrawnAbove()
    {
        var edit = new ScreenFieldEdit(0, "Chat", 4, null, Choices: Routes);
        var drawn = ScreenChrome.Choices(Column(9, 7, edit), edit, 56);

        var menu = drawn.Select((line, i) => (line, i)).Where(x => IsMenu(x.line)).Select(x => x.i).ToList();

        await Assert.That(menu).IsNotEmpty();
        await Assert.That(menu.Max()).IsLessThan(7);
        await Assert.That(drawn.Count).IsEqualTo(9);

        // The caption keeps the edge nearest the field, and names the direction it opened in.
        await Assert.That(drawn[menu.Max()]).Contains("▴");
    }

    // ---- through the screens that use it ------------------------------------------------------

    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

    private static ConsoleKeyInfo Char(char c) => new(c, ConsoleKey.NoName, false, false, false);

    private static List<TriggerSet> Sets() => new()
    {
        new TriggerSet
        {
            Name = "Comms",
            Triggers = new List<Trigger>
            {
                new() { Name = "Tell", Pattern = "tells you", Actions = new TriggerActions { SpawnTarget = "Chat" } },
            },
        },
    };

    /// <summary>Opens F2's route field — the rule row's third — and hands back the live session.</summary>
    private static SettingsSession OpenTheRoute(IReadOnlyList<TriggerSet> sets)
    {
        var session = new SettingsSession(
            selection => TriggersScreenRenderer.Model(sets, selection.CursorIn(0), Routes[1..]));
        session.Handle(Key(ConsoleKey.Enter)); // name
        session.Handle(Key(ConsoleKey.Tab));   // pattern
        session.Handle(Key(ConsoleKey.Tab));   // route
        return session;
    }

    [Test]
    public async Task F2DrawsTheRouteListUnderTheRouteField()
    {
        var sets = Sets();
        var editor = TriggersScreenRenderer.EditorColumn(sets, 0, Routes[1..], OpenTheRoute(sets).Focus());

        await Assert.That(Entries(editor)).IsEquivalentTo(new[] { "main", "Chat", "pages", "trade" });
        await Assert.That(Marked(editor)).IsEqualTo("Chat");
        await Assert.That(Caption(editor)).Contains(ScreenChrome.OpenChoicesCaption);
    }

    /// <summary>
    /// F5's log format is the app's one genuinely closed list, and it sits second-from-last in its
    /// block — so it is also the case that has to open upward. Both halves are asserted here because
    /// they are the two things the F2 case cannot show.
    /// </summary>
    [Test]
    public async Task F5DrawsTheLogFormatAsAClosedListAboveTheField()
    {
        var worlds = new List<WorldDefinition>
        {
            new()
            {
                Name = "Aardwolf",
                Host = "aardmud.org",
                Characters = new List<CharacterDefinition> { new() { Name = "Kaz" } },
            },
        };
        var edit = new ScreenFieldEdit(
            WorldsScreenRenderer.LogFormatField,
            "Html",
            4,
            null,
            Choices: Enum.GetNames<LogFormat>(),
            ClosedChoices: true);
        var focus = new ScreenFocus(WorldsScreenRenderer.CharactersPane, 0, edit);

        var plain = WorldsScreenRenderer.FormColumn(worlds[0].Characters[0], ScreenPalette.Accent, null, 0);
        var form = WorldsScreenRenderer.FormColumn(worlds[0].Characters[0], ScreenPalette.Accent, focus, 0);

        await Assert.That(Entries(form)).IsEquivalentTo(new[] { "None", "Plain", "Html", "Both" });
        await Assert.That(Marked(form)).IsEqualTo("Html");
        await Assert.That(Caption(form)).Contains(ScreenChrome.ClosedChoicesCaption);

        // Drawn above, and — the point of an overlay on this screen — over exactly as many rows.
        var field = form.FindIndex(
            l => l.Contains($"[{ScreenPalette.Ink} on {ScreenPalette.Accent}]", StringComparison.Ordinal));
        await Assert.That(form.Count).IsEqualTo(plain.Count);
        await Assert.That(field).IsGreaterThan(0);
        await Assert.That(form.FindLastIndex(IsMenu)).IsLessThan(field);
    }

    /// <summary>
    /// ↑↓ walk the list the user is looking at, not the one behind it: after typing <c>pa</c> the list
    /// holds only <c>pages</c>, and ↓ takes it. Filtering and stepping being the same list is what makes
    /// "type a bit, then arrow onto it" work at all.
    /// </summary>
    [Test]
    public async Task ArrowsWalkTheNarrowedList()
    {
        var sets = Sets();
        var session = OpenTheRoute(sets);

        for (var i = 0; i < "Chat".Length; i++)
        {
            session.Handle(Key(ConsoleKey.Backspace));
        }

        session.Handle(Char('p'));
        session.Handle(Char('a'));
        await Assert.That(session.Focus().Edit!.Value.VisibleChoices).IsEquivalentTo(new[] { "pages" });

        session.Handle(Key(ConsoleKey.DownArrow));
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("pages");
    }

    /// <summary>
    /// The other half of that bargain: a buffer that matched nothing is a name being typed for the
    /// first time, and an arrow key must not eat it. This is the case a cycling field got wrong —
    /// ↓ used to jump to the first choice and throw the typing away.
    /// </summary>
    [Test]
    public async Task ArrowsLeaveAFreshNameAlone()
    {
        var sets = Sets();
        var session = OpenTheRoute(sets);

        for (var i = 0; i < "Chat".Length; i++)
        {
            session.Handle(Key(ConsoleKey.Backspace));
        }

        foreach (var c in "combat")
        {
            session.Handle(Char(c));
        }

        session.Handle(Key(ConsoleKey.DownArrow));
        session.Handle(Key(ConsoleKey.UpArrow));
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("combat");

        // And it still commits: the list is a suggestion, and the field takes the name it was given.
        session.Handle(Key(ConsoleKey.Enter));
        await Assert.That(session.IsEditing).IsFalse();
        await Assert.That(sets[0].Triggers[0].Actions.SpawnTarget).IsEqualTo("combat");
    }

    /// <summary>
    /// A listed field is still a text field. Typing over the whole buffer, moving the caret and
    /// backspacing all behave exactly as they do on a free-text one — the list is drawn beside the
    /// keyboard, it does not take it.
    /// </summary>
    [Test]
    public async Task AListedFieldIsStillTypeable()
    {
        var sets = Sets();
        var session = OpenTheRoute(sets);

        session.Handle(Key(ConsoleKey.Home));
        session.Handle(Char('#'));
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("#Chat");

        session.Handle(Key(ConsoleKey.Delete));
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("#hat");

        session.Handle(Key(ConsoleKey.End));
        session.Handle(Key(ConsoleKey.Backspace));
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo("#ha");
    }
}
