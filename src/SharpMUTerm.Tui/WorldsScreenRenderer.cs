using System.Globalization;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Text;
using static SharpMUTerm.Tui.MarkupText;
using static SharpMUTerm.Tui.ScreenPalette;

namespace SharpMUTerm.Tui;

/// <summary>
/// Produces the markup sub-blocks for the F5 Worlds &amp; Characters screen — the header band, the
/// WORLDS list, the world/characters detail column, the character form, the assigned-trigger-set
/// list, and the footer action bar. <see cref="WorldsScreenView"/> composes these into real panels
/// (grids) for the live/snapshot view; <see cref="Render"/> merges the same blocks into a single
/// line list for the unit tests. Pure so every block is testable.
/// </summary>
internal static class WorldsScreenRenderer
{
    private const int LeftColumnWidth = 28;
    private const int WorldLabelWidth = 9;
    private const int CharLabelWidth = 10;
    private const int CharDetailColumnWidth = 40;

    /// <summary>How wide the cursor bar runs across a character row — the row's own column header.</summary>
    private const int CharacterRowWidth = 62;
    private const string DividerGlyph = " │ ";

    /// <summary>
    /// Which item of a list a pane's cursor has selected. The cursor also visits the pane's button
    /// rows, which sit past the end of the list, and a cursor parked on <c>[[+ world]]</c> must not
    /// read as "no world selected" — that would blank the detail column, empty the character pane, and
    /// take the <c>[[- del]]</c> row out from under the very cursor trying to reach it. A cursor past
    /// the end therefore keeps the last item selected. A negative cursor still means nothing is
    /// selected, which is how a caller says so deliberately.
    /// </summary>
    private static int Selected(int count, int cursor) => cursor >= count ? count - 1 : cursor;

    /// <summary>
    /// A raw pane-cursor pair resolved to the world and character they actually select, so every block
    /// of this screen — and the view that composes them — reads the same pair. Both panes end in button
    /// rows, so both cursors can point past their list.
    /// </summary>
    internal static (int World, int Character) Resolve(
        IReadOnlyList<WorldDefinition> worlds, int selectedWorld, int selectedCharacter)
    {
        ArgumentNullException.ThrowIfNull(worlds);

        var world = Selected(worlds.Count, selectedWorld);
        return (world, world >= 0 ? Selected(worlds[world].Characters.Count, selectedCharacter) : -1);
    }

    /// <summary>The accent hex for the selected world (its own, or the default teal).</summary>
    internal static string AccentFor(IReadOnlyList<WorldDefinition> worlds, int selectedWorld)
    {
        ArgumentNullException.ThrowIfNull(worlds);

        var world = Selected(worlds.Count, selectedWorld);
        return world >= 0 ? Hex(worlds[world].Accent) : Accent;
    }

    /// <summary>
    /// Merges every sub-block into one line list (header, worlds list | detail, character form |
    /// trigger sets, footer). Used by the unit tests and as a width-agnostic fallback; the live view
    /// composes the same blocks into panels instead. <paramref name="width"/>/<paramref name="height"/>
    /// &gt; 0 lay the merged form out full-console (bands + footer at the last row).
    /// </summary>
    public static List<string> Render(
        IReadOnlyList<WorldDefinition> worlds,
        IReadOnlyList<TriggerSet> triggerSets,
        int selectedWorld,
        int selectedCharacter,
        int width = 0,
        int height = 0)
    {
        ArgumentNullException.ThrowIfNull(worlds);
        ArgumentNullException.ThrowIfNull(triggerSets);

        (selectedWorld, selectedCharacter) = Resolve(worlds, selectedWorld, selectedCharacter);
        var accent = AccentFor(worlds, selectedWorld);
        var model = Model(worlds, triggerSets, selectedWorld, selectedCharacter);
        var lines = new List<string> { Band(HeaderLine(width, model), HeaderBg, width) };
        lines.AddRange(MergeColumns(WorldsColumn(worlds, selectedWorld),
            DetailColumn(worlds, triggerSets, selectedWorld, selectedCharacter, accent), LeftColumnWidth));

        if (HasCharacter(worlds, selectedWorld, selectedCharacter))
        {
            var character = worlds[selectedWorld].Characters[selectedCharacter];
            lines.Add(string.Empty);
            lines.Add(Band($"[{Rule}]{new string('─', width > 4 ? width - 2 : 60)}[/]", EditBg, width));
            foreach (var row in MergeColumns(FormColumn(character, accent, null, selectedCharacter),
                TriggersColumn(character, triggerSets, accent), CharDetailColumnWidth))
            {
                lines.Add(Band(" " + row, EditBg, width));
            }
        }

        if (height > 0)
        {
            while (lines.Count < height - 1)
            {
                lines.Add(string.Empty);
            }
        }

        lines.Add(Band(FooterLine(worlds, selectedWorld, selectedCharacter, accent, width), FooterBg, width));
        return lines;
    }

    internal static bool HasCharacter(IReadOnlyList<WorldDefinition> worlds, int selectedWorld, int selectedCharacter)
    {
        ArgumentNullException.ThrowIfNull(worlds);

        var world = Selected(worlds.Count, selectedWorld);
        return world >= 0 && Selected(worlds[world].Characters.Count, selectedCharacter) >= 0;
    }

    /// <summary>
    /// The screen title on the left, the keyboard hints right-aligned to <paramref name="width"/>. The
    /// hints are derived from <paramref name="model"/> and <paramref name="focus"/> rather than
    /// written here, so the header cannot advertise an edit the screen doesn't offer.
    /// </summary>
    internal static string HeaderLine(int width, ScreenModel? model = null, ScreenFocus? focus = null)
    {
        var title = $"[bold {Value}] Worlds & Characters[/]";
        var hints = ScreenChrome.Hints(
            ScreenChrome.ListHints, "F5", model?.HasEditableRow ?? false, focus, model?.HasRemovableRow ?? false);
        return SpreadLR(" " + title, hints, width);
    }

    /// <summary>The wire encodings a world may be set to; the detail column cycles them with ↑↓.</summary>
    private static readonly string[] Encodings = { "UTF-8", "ISO-8859-1", "ASCII", "CP437", "CP1252" };

    /// <summary>The label the WORLDS list's add button carries, and the row the renderer draws for it.</summary>
    internal const string AddWorldLabel = "+ world";

    /// <summary>The label the WORLDS list's delete button carries.</summary>
    internal const string RemoveWorldLabel = "- del";

    /// <summary>The labels the character list's buttons carry, in the order they are drawn.</summary>
    internal const string AddCharacterLabel = "+ add character";

    internal const string DuplicateCharacterLabel = "⧉ duplicate";

    internal const string RemoveCharacterLabel = "- remove";

    /// <summary>
    /// The screen's three navigable panes, in ⇥ order: the WORLDS list (no checkbox on a world's row,
    /// but ⏎ opens the world's own fields — the ones the detail column lists), the selected world's
    /// characters (Space flips auto-login, ⏎ edits the character's name and on-connect line), and the
    /// selected character's assigned trigger sets (Space assigns/unassigns). The last two collapse to
    /// empty when there is nothing selected above them, and ⇥ skips empty panes, so the cursor never
    /// lands somewhere with no rows.
    /// <para>
    /// A world's fields hang off its list row rather than becoming a pane of their own: the detail
    /// column is a projection of whatever the WORLDS list has selected, so its values already belong
    /// to that row, and a fourth pane would put ⇥ somewhere the eye doesn't go.
    /// </para>
    /// <para>
    /// Each list pane ends in its own buttons, because a button acts on the list it is drawn under and
    /// the cursor is already there. A button that would act on nothing is left out rather than drawn
    /// dead: a world with no characters offers <c>+ add character</c> and nothing else, so ⏎ never
    /// lands on a row that silently does nothing.
    /// </para>
    /// </summary>
    internal static ScreenModel Model(
        IReadOnlyList<WorldDefinition> worlds,
        IReadOnlyList<TriggerSet> triggerSets,
        int selectedWorld,
        int selectedCharacter)
    {
        ArgumentNullException.ThrowIfNull(worlds);
        ArgumentNullException.ThrowIfNull(triggerSets);

        (selectedWorld, selectedCharacter) = Resolve(worlds, selectedWorld, selectedCharacter);

        var worldRows = ScreenModel.Rows(worlds, w => ScreenRow.Of(
            ScreenField.Name("name", () => w.Name, v => w.Name = v),
            ScreenField.Text("host", () => w.Host, v => w.Host = v),
            ScreenField.Integer("port", () => w.Port, v => w.Port = v, 1, 65535),
            ScreenField.Choice("encoding", () => w.Encoding, v => w.Encoding = v, Encodings),
            ScreenField.Integer("keepalive", () => w.KeepaliveSeconds, v => w.KeepaliveSeconds = v, 0, 86400)))
            .Concat(WorldButtons(worlds, selectedWorld))
            .ToArray();

        var world = selectedWorld >= 0 && selectedWorld < worlds.Count ? worlds[selectedWorld] : null;
        var characterRows = world is null
            ? Array.Empty<ScreenRow>()
            : ScreenModel.Rows(world.Characters, c => ScreenRow.Of(
                ScreenToggle.Bind(() => c.AutoLogin, v => c.AutoLogin = v),
                ScreenField.Name("name", () => c.Name, v => c.Name = v),
                ScreenField.Optional("on connect", () => c.OnConnect, v => c.OnConnect = v)))
                .Concat(CharacterButtons(world, selectedCharacter))
                .ToArray();

        if (!HasCharacter(worlds, selectedWorld, selectedCharacter))
        {
            return new ScreenModel(worldRows, characterRows, Array.Empty<ScreenRow>());
        }

        var character = worlds[selectedWorld].Characters[selectedCharacter];
        var setRows = new ScreenRow[triggerSets.Count];
        for (var i = 0; i < triggerSets.Count; i++)
        {
            var name = triggerSets[i].Name;

            // Assignment is list membership, and the character's own order decides which set wins a
            // conflict (see AppConfiguration.ResolveTriggerSets) — so the snapshot restores the whole
            // list rather than re-adding the name at the end, which would silently reorder priority.
            setRows[i] = ScreenRow.Of(new ScreenToggle(
                () => character.TriggerSets.Contains(name),
                () =>
                {
                    if (!character.TriggerSets.Remove(name))
                    {
                        character.TriggerSets.Add(name);
                    }
                },
                () =>
                {
                    var previous = character.TriggerSets.ToList();
                    return () =>
                    {
                        character.TriggerSets.Clear();
                        character.TriggerSets.AddRange(previous);
                    };
                }));
        }

        return new ScreenModel(worldRows, characterRows, setRows);
    }

    /// <summary>
    /// The WORLDS list's buttons. Deleting is offered only when there is a world under the cursor to
    /// delete; a brand-new world is a blank template, because a world's whole identity is its host and
    /// a "helpfully" prefilled one would be a guess the user then has to notice and undo.
    /// </summary>
    private static List<ScreenRow> WorldButtons(IReadOnlyList<WorldDefinition> worlds, int selectedWorld)
    {
        var rows = new List<ScreenRow>();
        // Arrays report IsReadOnly through IList<T>, and a renderer handed one (the unit tests, any
        // caller with a fixed projection) must not offer a button whose only effect would be to throw.
        if (worlds is not IList<WorldDefinition> { IsReadOnly: false } list)
        {
            return rows;
        }

        rows.Add(ScreenRow.Of(ScreenButton.Add(AddWorldLabel, list, () => new WorldDefinition())));
        if (selectedWorld >= 0 && selectedWorld < list.Count)
        {
            rows.Add(ScreenRow.Of(ScreenButton.Remove(
                RemoveWorldLabel, list, selectedWorld, target: list[selectedWorld].Name)));
        }

        return rows;
    }

    /// <summary>
    /// The character list's buttons. Duplicating deep-copies through
    /// <see cref="CharacterDefinition.Clone"/> — an aliased copy would share its trigger-set list and
    /// its logging settings with the original, so editing one would silently edit both — and the copy
    /// is renamed rather than left as a second identical name, because a session is keyed
    /// <c>world.character</c> and two of those would collide.
    /// </summary>
    private static List<ScreenRow> CharacterButtons(WorldDefinition world, int selectedCharacter)
    {
        var characters = world.Characters;
        var rows = new List<ScreenRow>
        {
            ScreenRow.Of(ScreenButton.Add(AddCharacterLabel, characters, () => new CharacterDefinition())),
        };

        if (selectedCharacter >= 0 && selectedCharacter < characters.Count)
        {
            var source = characters[selectedCharacter];
            rows.Add(ScreenRow.Of(ScreenButton.Add(
                DuplicateCharacterLabel,
                characters,
                () =>
                {
                    var copy = source.Clone();
                    copy.Name = ScreenLists.Unique(characters.Select(c => c.Name), source.Name);
                    return copy;
                },
                target: source.Name)));
            rows.Add(ScreenRow.Of(ScreenButton.Remove(
                RemoveCharacterLabel, characters, selectedCharacter, target: source.Name)));
        }

        return rows;
    }

    internal static string FooterLine(
        IReadOnlyList<WorldDefinition> worlds,
        int selectedWorld,
        int selectedCharacter,
        string accent,
        int width,
        ScreenFocus? focus = null)
    {
        (selectedWorld, selectedCharacter) = Resolve(worlds, selectedWorld, selectedCharacter);
        var context = string.Empty;
        if (worlds.Count > 0 && selectedWorld >= 0)
        {
            var chars = worlds[selectedWorld].Characters.Count;
            context = ScreenChrome.Context(
                ScreenChrome.Position("world", selectedWorld, worlds.Count),
                chars > 0 && selectedCharacter >= 0
                    ? ScreenChrome.Position("character", selectedCharacter, chars)
                    : null);
        }

        var actions = ScreenChrome.Actions(accent, focus);
        return SpreadLR(" " + context, actions, width);
    }

    internal static List<string> WorldsColumn(
        IReadOnlyList<WorldDefinition> worlds, int selectedWorld, ScreenFocus? focus = null)
    {
        var cursor = focus ?? ScreenFocus.None;
        selectedWorld = Selected(worlds.Count, selectedWorld);
        var left = new List<string> { $"[{Label}]WORLDS[/]", string.Empty };

        for (var i = 0; i < worlds.Count; i++)
        {
            if (i > 0)
            {
                left.Add(string.Empty);
            }

            var world = worlds[i];
            var selected = i == selectedWorld;
            var marker = selected ? $"[bold {Accent}]▸[/]" : " ";
            var accentHex = Hex(world.Accent);
            var name = selected ? $"[bold {Value}]{Escape(world.Name)}[/]" : $"[{Value}]{Escape(world.Name)}[/]";
            left.Add(ScreenChrome.Cursor(
                $"{marker} [{accentHex}]▚[/] {name}", cursor.IsOn(0, i), LeftColumnWidth));
            left.Add($"    [{Label}]{Escape(world.Host)}:{world.Port.ToString(CultureInfo.InvariantCulture)}[/]");
            left.Add($"    [{Label}]{world.Characters.Count.ToString(CultureInfo.InvariantCulture)} chars[/]");
        }

        if (worlds.Count == 0)
        {
            left.Add($"[{Label}]no worlds[/]");
        }

        left.Add(string.Empty);
        left.AddRange(ScreenChrome.Buttons(
            WorldButtons(worlds, selectedWorld), cursor, 0, worlds.Count, LeftColumnWidth));
        return left;
    }

    internal static List<string> DetailColumn(
        IReadOnlyList<WorldDefinition> worlds,
        IReadOnlyList<TriggerSet> triggerSets,
        int selectedWorld,
        int selectedCharacter,
        string accent,
        ScreenFocus? focus = null)
    {
        var cursor = focus ?? ScreenFocus.None;
        (selectedWorld, selectedCharacter) = Resolve(worlds, selectedWorld, selectedCharacter);
        if (selectedWorld < 0)
        {
            return new List<string>();
        }

        var world = worlds[selectedWorld];

        // The world's own fields are the WORLDS-list row's fields, in this order — the detail column is
        // where they are displayed, so it is where an open edit draws its caret.
        var right = new List<string>
        {
            $"[bold {Value}]{Escape(world.Name)}[/]  [{Label}]{Escape(world.Host)}:{world.Port.ToString(CultureInfo.InvariantCulture)}[/]"
            + $"  [{accent}]TLS {OnOff(world.UseTls)}[/][{Label}] · {Escape(world.Encoding)}[/]",
            string.Empty,
            $"[{accent}]├ WORLD[/]",
            WorldField("name", Field($"[{Value}]{Escape(world.Name)}[/]", cursor, selectedWorld, 0)),
            WorldField("host", Field($"[{Value}]{Escape(world.Host)}[/]", cursor, selectedWorld, 1)),
            WorldField("port", Field(
                $"[{Value}]{world.Port.ToString(CultureInfo.InvariantCulture)}[/]", cursor, selectedWorld, 2)),
            WorldField("security", ScreenChrome.ReadOnly(Security(world))),
            WorldField("encoding", Field($"[{Value}]{Escape(world.Encoding)}[/]", cursor, selectedWorld, 3)),
            WorldField("keepalive", Field(
                world.KeepaliveSeconds > 0
                    ? $"[{Value}]{world.KeepaliveSeconds.ToString(CultureInfo.InvariantCulture)}s[/]"
                    : $"[{Label}]off[/]",
                cursor,
                selectedWorld,
                4)),
            string.Empty,
            $"[{accent}]├ CHARACTERS[/]   [{Label}]a character is a connection[/]",
            $"[{Label}]  name          state       login        trigger sets[/]",
        };

        if (world.Characters.Count == 0)
        {
            right.Add($"[{Label}]no characters — this world has nothing to connect with.[/]");
        }
        else
        {
            for (var i = 0; i < world.Characters.Count; i++)
            {
                right.Add(ScreenChrome.Cursor(
                    CharacterRow(world.Characters[i], i == selectedCharacter),
                    cursor.IsOn(1, i),
                    CharacterRowWidth));
            }
        }

        right.Add(string.Empty);
        right.AddRange(ScreenChrome.Buttons(
            CharacterButtons(world, selectedCharacter), cursor, 1, world.Characters.Count, CharacterRowWidth));
        return right;
    }

    /// <summary>
    /// The character form — labels left-aligned with their values, one field per row. The editable
    /// ones are the character row's own fields (name, then on-connect) and are the only two drawn in a
    /// field well. The other three deliberately are not: the password is
    /// <see cref="System.Text.Json.Serialization.JsonIgnoreAttribute"/>d and belongs in a credential
    /// store, auto-login is a readout of the character row's own checkbox, and the session line is a
    /// report of what the connection is doing rather than a setting at all.
    /// </summary>
    internal static List<string> FormColumn(
        CharacterDefinition character, string accent, ScreenFocus? focus = null, int selectedCharacter = -1)
    {
        var cursor = focus ?? ScreenFocus.None;
        return new List<string>
        {
            $"[bold {accent}]└ CHARACTER · {Escape(character.Name)}[/]",
            string.Empty,
            CharField("name", Field(
                $"[{Value}]{Escape(character.Name)}[/]", cursor, selectedCharacter, 0, pane: 1)),
            CharField("password", $"{ScreenChrome.ReadOnly("••••••••")} [{Label}]keychain[/]"),
            CharField("on connect", Field(
                $"[{Value}]{Escape(character.OnConnect ?? "—")}[/]", cursor, selectedCharacter, 1, pane: 1)),
            CharField("auto-login", ScreenChrome.ReadOnly(character.AutoLogin ? "yes" : "no")),
            CharField("session", ScreenChrome.ReadOnly("offline")),
        };
    }

    /// <summary>Draws a value as a field, showing the buffer and caret when its edit is the open one.</summary>
    private static string Field(string display, ScreenFocus cursor, int index, int field, int pane = 0) =>
        ScreenChrome.Field(display, cursor.EditOn(pane, index, field));

    /// <summary>The assigned-trigger-sets checklist for a character.</summary>
    internal static List<string> TriggersColumn(
        CharacterDefinition character,
        IReadOnlyList<TriggerSet> triggerSets,
        string accent,
        ScreenFocus? focus = null)
    {
        var cursor = focus ?? ScreenFocus.None;
        var rows = new List<string>(triggerSets.Count);
        foreach (var set in triggerSets)
        {
            var assigned = character.TriggerSets.Contains(set.Name);
            var box = assigned ? $"[{accent}][[x]][/]" : $"[{Label}][[ ]][/]";
            var nameColor = assigned ? Value : Label;
            var description = Escape(set.Description ?? string.Empty);
            rows.Add(
                $"{box} [{nameColor}]▪ {Escape(set.Name)}[/] [{Label}]— {description}   {set.Triggers.Count.ToString(CultureInfo.InvariantCulture)} rules[/]");
        }

        // The checklist sits in an auto-width column (see WorldsScreenView), so a cursor bar sized to
        // one row would widen the block and shunt it sideways as the cursor moved. Sizing every bar to
        // the widest row keeps the column's measured width constant whatever is focused.
        var barWidth = rows.Count == 0 ? 0 : rows.Max(VisibleLength);

        var list = new List<string> { $"[{Label}]assigned trigger sets[/]", string.Empty };
        for (var i = 0; i < rows.Count; i++)
        {
            list.Add(ScreenChrome.Cursor(rows[i], cursor.IsOn(2, i), barWidth));
        }

        return list;
    }

    private static string CharacterRow(CharacterDefinition character, bool selected)
    {
        var marker = selected ? $"[bold {Accent}]▸[/]" : " ";
        var name = PadVisible($"[{(selected ? "bold " : string.Empty)}{Value}]{Escape(character.Name)}[/]", 13);
        var login = PadVisible(character.AutoLogin ? "auto-login" : "manual", 12);
        var sets = Escape(string.Join(", ", character.TriggerSets));
        return $"{marker} {name} [{Label}]○ offline[/]  [{Label}]{login}[/] [{Label}]{sets}[/]";
    }

    private static string Security(WorldDefinition world) =>
        world.UseTls
            ? $"TLS on · certs {(world.AllowInvalidCertificates ? "lax" : "strict")}"
            : "TLS off";

    private static string WorldField(string label, string value) =>
        $"  [{Label}]{label.PadLeft(WorldLabelWidth)}[/]  {value}";

    private static string CharField(string label, string value) =>
        $"  [{Label}]{label.PadRight(CharLabelWidth)}[/]  {value}";

    private static string OnOff(bool value) => value ? "on" : "off";

    private static string Band(string inner, string bg, int width)
    {
        if (width <= 0)
        {
            return inner;
        }

        var pad = Math.Max(0, width - VisibleLength(inner));
        return $"[on {bg}]{inner}{new string(' ', pad)}[/]";
    }

    private static List<string> MergeColumns(IReadOnlyList<string> left, IReadOnlyList<string> right, int leftWidth)
    {
        var count = Math.Max(left.Count, right.Count);
        var merged = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var l = i < left.Count ? left[i] : string.Empty;
            var r = i < right.Count ? right[i] : string.Empty;
            merged.Add(PadVisible(l, leftWidth) + $"[{Rule}]{DividerGlyph}[/]" + r);
        }

        return merged;
    }

    /// <summary>
    /// A world's accent as markup. Shared with the F2 swatches through <see cref="ScreenColours"/>, so
    /// an indexed accent resolves to the colour the terminal will actually paint instead of collapsing
    /// to the app's default teal; only the terminal *default* has no hex of its own.
    /// </summary>
    private static string Hex(TerminalColor color) => ScreenColours.Hex(color, Accent);
}
