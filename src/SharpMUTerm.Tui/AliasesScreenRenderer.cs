using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using static SharpMUTerm.Tui.MarkupText;
using static SharpMUTerm.Tui.ScreenPalette;

namespace SharpMUTerm.Tui;

/// <summary>
/// Produces the markup sub-blocks for the F3 Aliases screen — the header band, the alias list
/// (flattened across every <see cref="TriggerSet"/>, each row carrying its enabled state,
/// name/pattern, owning set, and expansion), the editor for the selected alias (pattern, expansion
/// lines, and the case-sensitivity toggle), and the footer action bar.
/// <see cref="AliasesScreenView"/> composes these into real panels (grids) for the live/snapshot
/// view; <see cref="Render"/> merges the same blocks into a single line list for the unit tests.
/// Pure so every block is testable.
/// </summary>
internal static class AliasesScreenRenderer
{
    /// <summary>
    /// Visible width of the left column when the screen can afford it. The view lays its column out at
    /// exactly the width it passes back in, so the two must agree -- a cursor bar padded narrower than
    /// its column leaves a gap before the rule. Shared rather than duplicated so they cannot drift apart.
    /// </summary>
    internal const int ColumnWidth = 56;

    /// <summary>The fewest cells the alias list is still worth drawing in.</summary>
    internal const int MinColumnWidth = 40;

    /// <summary>The fewest cells the editor pane can be read in — its widest heading and then some.</summary>
    internal const int MinEditorWidth = 32;

    /// <summary>
    /// What the alias list's key is called, and the two marks it glosses. A row reads
    /// <c>✓ ▸ say ^'(.*) ▪ Comms → say $1</c>: the tick is the enabled state <c>on</c> heads without
    /// really explaining, and the square is the owning set. Named constants because the row draws the
    /// same glyphs and the key would otherwise be a second, drifting copy of them.
    /// </summary>
    private const string LegendLabel = "key";

    private const string EnabledGlyph = "✓";

    private const string SetGlyph = "▪";

    /// <summary>
    /// The alias row's field ordinals, in the order ⇥ steps through them. The name leads, as it does on
    /// every list screen: ⏎ on an alias — including one just created — opens the value that tells it
    /// apart from its neighbours. Named rather than written as literals because the renderer, the model
    /// and the tests all address the same ordinals.
    /// </summary>
    internal const int NameField = 0;

    internal const int PatternField = 1;

    internal const int ExpansionField = 2;

    /// <summary>
    /// Which <see cref="TriggerSet"/> the alias lives in, appended last so the ordinals above keep
    /// meaning what they meant. Committing it moves the alias — see <see cref="ScreenLists.Owner{T}"/>.
    /// </summary>
    internal const int SetField = 3;

    /// <summary>The labels the alias list's buttons carry, in the order they are drawn.</summary>
    internal const string AddAliasLabel = "+ alias";

    internal const string DuplicateAliasLabel = "⧉ duplicate";

    /// <summary>
    /// What a brand-new alias is called, matches and expands to. None of the three may be blank —
    /// <see cref="ScreenField.Pattern"/> refuses an empty regex and <see cref="ScreenField.Lines"/> an
    /// empty expansion — so a new alias is a working placeholder rather than an empty shell that
    /// couldn't be committed. It is left enabled because an alias only acts on input the user types.
    /// </summary>
    private const string NewAliasName = "New Alias";

    private const string NewAliasPattern = "^alias$";

    private const string NewAliasSubstitution = "look";

    /// <summary>
    /// Merges every sub-block into one line list (header, alias list | editor, footer). Used by the
    /// unit tests and as a width-agnostic fallback; the live view composes the same blocks into
    /// panels instead.
    /// </summary>
    public static List<string> Render(IReadOnlyList<TriggerSet> sets, int selected)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var left = ListColumn(sets, selected);
        var right = EditorColumn(sets, selected);

        var lines = new List<string> { HeaderLine(0, Model(sets, selected)), string.Empty };

        var rowCount = Math.Max(left.Count, right.Count);
        for (var i = 0; i < rowCount; i++)
        {
            var leftLine = i < left.Count ? left[i] : string.Empty;
            var rightLine = i < right.Count ? right[i] : string.Empty;
            lines.Add($"{PadVisible(leftLine, ColumnWidth)} │ {rightLine}");
        }

        lines.Add(string.Empty);
        lines.Add(FooterLine(sets, selected, 0));

        return lines;
    }

    /// <summary>
    /// The screen title on the left, the keyboard hints right-aligned to <paramref name="width"/>. The
    /// hints are derived from <paramref name="model"/> and <paramref name="focus"/> rather than
    /// written here, so the header cannot advertise an edit the screen doesn't offer.
    /// </summary>
    internal static string HeaderLine(int width, ScreenModel? model = null, ScreenFocus? focus = null)
    {
        var title = $"[bold {Value}] Aliases[/]";
        var hints = ScreenChrome.Hints(
            ScreenChrome.ListHints, "F3", model?.HasEditableRow ?? false, focus, model?.HasRemovableRow ?? false);
        return SpreadLR(" " + title, hints, width);
    }

    /// <summary>
    /// The screen's navigable panes: the alias list (Space enables/disables one, ⏎ edits its pattern
    /// and then — with ⇥ — its expansion) and the selected alias's checkbox rows, in the order
    /// <see cref="EditorColumn"/> draws them. Both values hang off the list row rather than becoming
    /// rows of their own, so the editor pane's cursor indices keep meaning what they meant.
    /// </summary>
    internal static ScreenModel Model(IReadOnlyList<TriggerSet> sets, int selected)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var entries = Flatten(sets);
        var list = ScreenModel.Rows(entries, entry => ScreenRow.Of(
            ScreenToggle.Bind(() => entry.Alias.Enabled, v => entry.Alias.Enabled = v),
            ScreenField.Name("name", () => entry.Alias.Name, v => entry.Alias.Name = v),
            ScreenField.Pattern("match pattern", () => entry.Alias.Pattern, v => entry.Alias.Pattern = v),
            ScreenField.Lines("expansion", () => entry.Alias.Substitution, v => entry.Alias.Substitution = v),
            ScreenLists.Owner(sets, s => s.Aliases, entry.Alias)))
            .Concat(Buttons(sets, selected))
            .ToArray();

        if (selected < 0 || selected >= entries.Count)
        {
            return new ScreenModel(list, Array.Empty<ScreenRow>());
        }

        var alias = entries[selected].Alias;
        var editor = new[]
        {
            ScreenRow.Of(ScreenToggle.Bind(() => alias.CaseSensitive, v => alias.CaseSensitive = v)),
        };

        return new ScreenModel(list, editor);
    }

    /// <summary>
    /// The alias list's buttons. Like F2's, an alias is added to the set that owns the selection, so a
    /// new one appears in the set the user is looking at rather than wherever the configuration ends.
    /// <para>
    /// <c>duplicate</c> is offered here for the same reason it is on F2 and not on F6: an alias carries
    /// a regex and a multi-line expansion, and aliases come in families that share the shape of both —
    /// copying one and changing a word is the real workflow. It copies through
    /// <see cref="Alias.Clone"/> and renames the copy so it can be told apart in the list.
    /// </para>
    /// </summary>
    private static List<ScreenRow> Buttons(IReadOnlyList<TriggerSet> sets, int selected)
    {
        var rows = new List<ScreenRow>();
        if (ScreenLists.Target(sets, s => s.Aliases, selected) is not { } target)
        {
            return rows;
        }

        rows.Add(ScreenRow.Of(ScreenButton.Add(
            AddAliasLabel,
            target.Items,
            () => new Alias
            {
                Name = NewAliasName,
                Pattern = NewAliasPattern,
                Substitution = NewAliasSubstitution,
            },
            target.Offset)));

        if (ScreenLists.Locate(sets, s => s.Aliases, selected) is not { } slot)
        {
            return rows;
        }

        var source = slot.Items[slot.Index];
        rows.Add(ScreenRow.Of(ScreenButton.Add(
            DuplicateAliasLabel,
            slot.Items,
            () =>
            {
                var copy = source.Clone();
                copy.Name = ScreenLists.Unique(sets.SelectMany(s => s.Aliases).Select(a => a.Name), source.Name);
                return copy;
            },
            slot.Offset,
            source.Name)));
        rows.Add(ScreenRow.Of(ScreenButton.Remove(
            slot.Items, slot.Index, slot.Offset, source.Name, () => $"alias {source.Name}")));

        return rows;
    }

    /// <summary>The action bar: which alias is selected on the left, cancel/save on the right.</summary>
    internal static string FooterLine(
        IReadOnlyList<TriggerSet> sets, int selected, int width, ScreenFocus? focus = null)
    {
        var entries = Flatten(sets);
        var context = string.Empty;
        if (entries.Count > 0 && selected >= 0 && selected < entries.Count)
        {
            context = ScreenChrome.Context(
                ScreenChrome.Position("alias", selected, entries.Count),
                "set " + Escape(entries[selected].SetName));
        }

        var actions = ScreenChrome.Actions(focus: focus);
        return SpreadLR(" " + context, actions, width);
    }

    /// <summary>The alias list — every alias of every set, with its enabled state and expansion.</summary>
    /// <param name="width">
    /// How wide the column actually runs, which the cursor bars are padded to — the view gives cells
    /// back to the editor on a narrow screen (see <see cref="ScreenChrome.SplitWidth"/>).
    /// </param>
    internal static List<string> ListColumn(
        IReadOnlyList<TriggerSet> sets, int selected, ScreenFocus? focus = null, int width = ColumnWidth)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var cursor = focus ?? ScreenFocus.None;
        var entries = Flatten(sets);
        var lines = new List<string> { "[dim]on  name / pattern → expansion[/]" };

        if (entries.Count == 0)
        {
            lines.Add("[dim]no aliases[/]");
        }

        // Walked per set rather than down the flattened list, so a set holding no aliases can still say
        // so — it owns none of these rows and would otherwise be drawn nowhere at all. The row indices
        // are the flattened ones either way: the placeholder is markup, not a cursor stop.
        var index = 0;
        foreach (var set in sets)
        {
            if (set.Aliases.Count == 0)
            {
                lines.Add(ScreenChrome.EmptySet(set.Name, "aliases"));
                continue;
            }

            foreach (var alias in set.Aliases)
            {
                lines.Add(ScreenChrome.Cursor(
                    Row(alias, set.Name, index == selected), cursor.IsOn(0, index), width));
                index++;
            }
        }

        lines.Add(string.Empty);
        lines.AddRange(ScreenChrome.Buttons(Buttons(sets, selected), cursor, 0, entries.Count, width));
        lines.Add(string.Empty);
        var picked = selected >= 0 && selected < entries.Count ? entries[selected].Alias : null;
        lines.AddRange(ScreenChrome.Legend(
            LegendLabel,
            new[]
            {
                ScreenChrome.LegendEntry(EnabledGlyph, "enabled", picked?.Enabled ?? false),
                ScreenChrome.LegendEntry(SetGlyph, "set", picked is not null),
            },
            width));
        return lines;
    }

    /// <summary>
    /// The editor for the selected alias — pattern, expansion lines, and the case-sensitivity
    /// toggle. Empty when nothing is selected.
    /// </summary>
    /// <param name="width">How wide the pane runs, which its cursor bars and dropdowns are sized to.</param>
    /// <param name="height">How many rows the pane has, or 0 when the caller has none.</param>
    internal static List<string> EditorColumn(
        IReadOnlyList<TriggerSet> sets,
        int selected,
        ScreenFocus? focus = null,
        int width = ColumnWidth,
        int height = 0)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var cursor = focus ?? ScreenFocus.None;
        var entries = Flatten(sets);
        return selected >= 0 && selected < entries.Count
            ? BuildEditor(entries[selected].Alias, entries[selected].SetName, cursor, selected, width, height)
            : new List<string>();
    }

    /// <summary>Flattens every set's aliases into one list, each paired with its owning set's name.</summary>
    private static List<(Alias Alias, string SetName)> Flatten(IReadOnlyList<TriggerSet> sets)
    {
        var entries = new List<(Alias, string)>();
        foreach (var set in sets)
        {
            foreach (var alias in set.Aliases)
            {
                entries.Add((alias, set.Name));
            }
        }

        return entries;
    }

    private static string Row(Alias alias, string setName, bool selected)
    {
        var check = alias.Enabled ? $"[{Accent}]✓[/]" : "[dim]·[/]";
        var marker = selected ? "▸" : " ";
        var name = Escape(alias.Name);
        var pattern = Escape(alias.Pattern);
        var expansion = Escape(FirstLine(alias.Substitution));
        return $"{check} {marker} [bold]{name}[/] [dim]{pattern}[/] [dim]▪ {Escape(setName)}[/] → {expansion}";
    }

    private static List<string> BuildEditor(
        Alias alias,
        string setName,
        ScreenFocus cursor,
        int selected,
        int width = ColumnWidth,
        int height = 0)
    {
        var set = cursor.EditOn(0, selected, SetField);

        // The name leads the editor because it leads the row's fields: ⏎ on an alias opens it here,
        // which is also where one created by [+ alias] lands ready to be called something. The set
        // follows it, because the list is flattened across every set and the two together are what
        // identify the alias; committing it *moves* the alias (ScreenLists.Owner).
        var lines = new List<string>
        {
            "[dim]name[/]",
            $"  {ScreenChrome.Field(Escape(alias.Name), cursor.EditOn(0, selected, NameField))}",
            string.Empty,
            "[dim]set[/]",
            $"  {ScreenChrome.Field($"[{Value}]{Escape(setName)}[/]", set)}",
            string.Empty,
            "[dim]match pattern (regex)[/]",
            $"  {ScreenChrome.Field(Escape(alias.Pattern), cursor.EditOn(0, selected, PatternField))}",
            string.Empty,
            "[dim]expands to[/]",
        };

        // An expansion is one command per line, so it normally lists. While it is being typed it is a
        // single buffer with the breaks written \n (see ScreenField.Lines), and it has to be drawn the
        // way it is being edited — one row — or the caret would have nowhere honest to sit.
        if (cursor.EditOn(0, selected, ExpansionField) is { } expansion)
        {
            lines.Add("  " + ScreenChrome.Field(string.Empty, expansion));
        }
        else
        {
            // Listed, the expansion is still one editable value, so it still gets one field well —
            // every row padded to the longest, or the well would be ragged and read as several.
            var commands = alias.Substitution.Split('\n').Select(Escape).ToList();
            var longest = commands.Count == 0 ? 0 : commands.Max(c => VisibleLength(c));
            lines.AddRange(commands.Select(c => "  " + ScreenChrome.Field(PadVisible(c, longest), null)));
        }

        lines.Add(string.Empty);
        var caseRow = alias.CaseSensitive
            ? $"[{Accent}][[x]][/] case sensitive"
            : "[dim][[ ]] case sensitive[/]";
        lines.Add(ScreenChrome.Cursor(caseRow, cursor.IsOn(1, 0), width));

        // Compacted before the dropdown is laid over it: the overlay replaces rows by index, so dropping
        // a blank underneath it afterwards would slide the list off the field it belongs to.
        ScreenChrome.Compact(lines, height);
        return ScreenChrome.Choices(lines, cursor.Edit, width);
    }

    private static string FirstLine(string text)
    {
        var newlineIndex = text.IndexOf('\n');
        return newlineIndex < 0 ? text : text[..newlineIndex];
    }
}
