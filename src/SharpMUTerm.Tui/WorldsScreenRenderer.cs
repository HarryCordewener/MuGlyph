using System.Globalization;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Telnet;
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

    /// <summary>
    /// How wide the cursor bar runs across a row of the detail column — a character row, or one of the
    /// world's security checkboxes. A row longer than this keeps its own width (the bar is padding, not
    /// a clip), which is how the certificate warning can run past the column the rest of them share.
    /// </summary>
    private const int DetailRowWidth = 62;
    private const string DividerGlyph = " │ ";

    /// <summary>
    /// The screen's panes, in ⇥ order. Named rather than written as literals because the renderer, the
    /// model, the app's seeding and the tests all address the same numbers.
    /// </summary>
    internal const int WorldsPane = 0;

    internal const int CharactersPane = 1;

    internal const int TriggerSetsPane = 2;

    /// <summary>
    /// The selected world's security checkboxes. Appended rather than inserted after the WORLDS list it
    /// belongs to: a pane index is a cursor coordinate, and renumbering the two panes below it would
    /// move every stop the screen (and its tests) already navigate by — the same rule that keeps a
    /// value's editor hanging off an existing row instead of a new one.
    /// </summary>
    internal const int SecurityPane = 3;

    /// <summary>
    /// The WORLDS-list row's field ordinals, in the order ⇥ steps through them, and the ordinals the
    /// detail column draws an open edit's caret at. Named constants rather than literals, because a
    /// field inserted anywhere but the end would otherwise silently draw the caret on the wrong row.
    /// </summary>
    internal const int WorldNameField = 0;

    internal const int HostField = 1;

    internal const int PortField = 2;

    internal const int EncodingField = 3;

    internal const int KeepaliveField = 4;

    /// <summary>
    /// The character row's field ordinals, in the order ⇥ steps through them — which is the order the
    /// CHARACTER form draws them in, top to bottom. The name leads, as it does on every list screen.
    /// <para>
    /// The password and the connect line are <em>inserted</em> after the name rather than appended past
    /// the log fields, and the ordinals below them moved to make room. Appending would have left the
    /// numbering untouched at the cost of ⇥ walking name → on connect → log → log folder and then back
    /// <em>up</em> the form to the password — the same "three hops forward and one backwards" that
    /// <see cref="PaneLayout"/> exists to have fixed one level up. Every renderer and every test
    /// addresses these by name, so the shift is a rename and not a silent renumbering.
    /// </para>
    /// </summary>
    internal const int CharacterNameField = 0;

    internal const int PasswordField = 1;

    internal const int ConnectStringField = 2;

    internal const int OnConnectField = 3;

    /// <summary>
    /// <see cref="CharacterDefinition.ConnectAtStartup"/>. Inserted here rather than appended for the
    /// reason above, and here in particular because it belongs with the connection rows it is about: the
    /// connect line, the on-connect lines and the login readout are all answers to "what happens when
    /// this character connects", and this one answers "does it, unasked".
    /// <para>
    /// <b>It stays a field, and that is now load-bearing.</b> A character row draws no checkbox — see
    /// <see cref="CharacterRow"/> — so a value moved onto the row's toggle is a value with no affordance
    /// at all. This one has a well, and ⏎ opens it, which is the only reachable way to set it.
    /// </para>
    /// </summary>
    internal const int StartupField = 4;

    internal const int LogFormatField = 5;

    internal const int LogDirectoryField = 6;

    /// <summary>
    /// <see cref="LoggingSettings.RestoreLog"/> — whether this character's panes come back holding their
    /// previous session's content. Appended after the two log rows rather than inserted among them,
    /// because it is the third answer to "what does this client write down about me" and reads in that
    /// order: what a transcript is, where it goes, and then whether the panes remember. It is a field
    /// and not a toggle for the same reason <see cref="StartupField"/> is — a character row draws no
    /// checkbox, so a value on the row's toggle would have no affordance at all.
    /// </summary>
    internal const int RestoreLogField = 7;

    /// <summary>
    /// The trigger-set row's field ordinals. A set's name leads, as every list row's does — and here it
    /// is more than a label: a character opts into automation <em>by name</em>, so renaming a set is a
    /// change with consequences elsewhere in the configuration (see <see cref="RenameSet"/>).
    /// </summary>
    internal const int SetNameField = 0;

    internal const int SetDescriptionField = 1;

    /// <summary>The F-key that opens this screen, and the second key that opens it on a character's log.</summary>
    internal const string FKey = "F5";

    internal const string LogFKey = "F9";

    /// <summary>
    /// Which item of a list a pane's cursor has selected. The cursor also visits the pane's
    /// <c>[[+ …]]</c> row, which sits past the end of the list, and a cursor parked there must not read
    /// as "no world selected" — that would blank the detail column and empty the character pane while
    /// the user was reaching for the add button. A cursor past the end therefore keeps the last item
    /// selected. A negative cursor still means nothing is selected, which is how a caller says so
    /// deliberately.
    /// <para>
    /// This clamp is a <em>display</em> rule and never decides what a removal acts on. The live screens
    /// hand these blocks <see cref="ScreenSelection.SelectionIn"/> — the anchored selection, which never
    /// runs past its list — and a removal is run by Delete on the selected row, whose own row is not a
    /// cursor stop at all (see <see cref="ScreenModel.Sizes"/>). It was reading this clamp as the cause of
    /// "only the last world can be deleted" that pointed one diagnosis at the wrong mechanism.
    /// </para>
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
        int height = 0,
        int selectedSet = 0)
    {
        ArgumentNullException.ThrowIfNull(worlds);
        ArgumentNullException.ThrowIfNull(triggerSets);

        (selectedWorld, selectedCharacter) = Resolve(worlds, selectedWorld, selectedCharacter);
        var accent = AccentFor(worlds, selectedWorld);
        var model = Model(worlds, triggerSets, selectedWorld, selectedCharacter, selectedSet);
        var lines = new List<string> { Band(HeaderLine(width, model), HeaderBg, width) };
        lines.AddRange(MergeColumns(WorldsColumn(worlds, selectedWorld),
            DetailColumn(worlds, triggerSets, selectedWorld, selectedCharacter, accent), LeftColumnWidth));

        if (HasCharacter(worlds, selectedWorld, selectedCharacter))
        {
            var character = worlds[selectedWorld].Characters[selectedCharacter];
            lines.Add(string.Empty);
            lines.Add(Band($"[{Rule}]{new string('─', width > 4 ? width - 2 : 60)}[/]", EditBg, width));
            foreach (var row in MergeColumns(FormColumn(character, accent, null, selectedCharacter),
                TriggersColumn(character, triggerSets, accent, null, selectedSet, worlds),
                CharDetailColumnWidth))
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
    /// <param name="fkey">
    /// The key that opened the screen, which is the key the header offers to close it with. It is a
    /// parameter rather than a constant because this screen has two doors: F5, and F9 straight onto the
    /// selected character's log. A header that always said <c>F5</c> would name a key that, pressed from
    /// an F9-opened screen, re-opens it somewhere else instead of closing it.
    /// </param>
    internal static string HeaderLine(
        int width, ScreenModel? model = null, ScreenFocus? focus = null, string fkey = FKey)
    {
        var title = $"[bold {Value}] Worlds & Characters[/]";
        var hints = ScreenChrome.Hints(
            ScreenChrome.ListHints, fkey, model?.HasEditableRow ?? false, focus, model?.HasRemovableRow ?? false);
        return SpreadLR(" " + title, hints, width);
    }

    /// <summary>
    /// The encodings a world may be set to; the detail column cycles them with ↑↓. <c>auto</c> leads
    /// and is the default: it follows CHARSET negotiation. Every other entry is an <em>override</em> —
    /// used whatever the server says — which is why the detail column labels them as one.
    /// </summary>
    private static readonly string[] Encodings =
        { TelnetSessionOptions.AutoEncodingName, "UTF-8", "ISO-8859-1", "ASCII", "CP437", "CP1252" };

    /// <summary>The label the WORLDS list's add button carries, and the row the renderer draws for it.</summary>
    internal const string AddWorldLabel = "+ world";

    /// <summary>The labels the character list's buttons carry, in the order they are drawn.</summary>
    internal const string AddCharacterLabel = "+ add character";

    internal const string DuplicateCharacterLabel = "⧉ duplicate";

    /// <summary>The label the trigger-set list's add button carries.</summary>
    internal const string AddSetLabel = "+ set";

    /// <summary>What a brand-new trigger set is called before it is renamed.</summary>
    internal const string NewSetName = "New Set";

    /// <summary>What an unset description reads as, so the row is visibly a place one goes.</summary>
    internal const string NoDescription = "—";

    /// <summary>
    /// The row for <see cref="CharacterDefinition.ConnectAtStartup"/>. Two cells short, because the
    /// CHARACTER panel is 48 wide and <c>CharField</c> has already spent fourteen of them on the indent
    /// and the label column — a row that wraps costs the form a line it was never measured for, which is
    /// how <c>log folder</c> once fell out of the band (see <see cref="StorageNote"/>).
    /// <para>
    /// It says <em>when</em>, and the <c>login</c> row directly under it says <em>what is typed</em>;
    /// they are adjacent so the difference is read rather than guessed, and neither label contains the
    /// other's word. <c>at start</c> in particular avoids "auto-": the one thing this must not be called
    /// is a second kind of login setting.
    /// </para>
    /// </summary>
    internal const string StartupLabel = "at start";

    /// <summary>Marked: this character is dialled when the client launches.</summary>
    internal const string StartupOn = "connect";

    /// <summary>
    /// Unmarked — the default. It reads <c>off</c> rather than <c>no</c> so it matches the world's
    /// <c>keepalive</c> row four lines above it, the screen's other "this feature is simply not on"
    /// value, and is drawn in the same muted ink for the same reason.
    /// </summary>
    internal const string StartupOff = "off";

    private static readonly string[] StartupChoices = { StartupOn, StartupOff };

    /// <summary>
    /// The row for <see cref="LoggingSettings.RestoreLog"/>. It says <c>restore</c> and not "restore
    /// log", because the two rows above it already carry the word <c>log</c> and mean the transcript by
    /// it; a third row wearing it would read as a third transcript setting, which is exactly what this
    /// is not. The note beside it is what carries the meaning.
    /// </summary>
    internal const string RestoreLabel = "restore";

    /// <summary>
    /// What the <c>restore</c> row says it does. Short because the CHARACTER panel is 48 cells and
    /// <c>CharField</c> has already spent fourteen of them on the indent and the label column — the
    /// same budget <see cref="StartupLabel"/> and <see cref="StorageNote"/> are written to, and the one
    /// a longer sentence here quietly overran until <c>ScreenReadOnlyTests</c> caught it.
    /// </summary>
    internal const string RestoreNote = "panes refill after a restart";

    /// <summary>The default: the panes come back.</summary>
    internal const string RestoreOn = "on";

    /// <summary>
    /// Opted out. It reads <c>off</c> to match <c>at start</c> and the world's <c>keepalive</c> row —
    /// the screen's other "this feature is simply not on" values — and is drawn in the same muted ink.
    /// </summary>
    internal const string RestoreOff = "off";

    private static readonly string[] RestoreChoices = { RestoreOn, RestoreOff };

    /// <summary>
    /// The screen's four navigable panes, in ⇥ order: the WORLDS list (no checkbox on a world's row,
    /// but ⏎ opens the world's own fields — the ones the detail column lists), the selected world's
    /// characters (no checkbox on a character's row either — ⏎ edits its name, password, connect line,
    /// on-connect line, <c>at start</c> and log), the
    /// <b>trigger sets</b> (Space assigns the selected character to one, ⏎ renames it, and the pane's
    /// own buttons make and unmake them), and the selected world's two security checkboxes. All but the
    /// first collapse to empty when there is nothing selected above them, and ⇥ skips empty panes, so
    /// the cursor never lands somewhere with no rows.
    /// <para>
    /// The trigger-set pane is where sets are <em>managed</em>, not merely assigned — it is the only
    /// view in the app of sets as objects, because F2, F3, F4 and F6 each flatten one <em>kind</em> of a
    /// set's contents across all of them and so cannot show a set that holds none of that kind. It still
    /// needs a selected character, because the other half of every row is that character's opt-in; on a
    /// configuration with no characters yet there is also nothing a set could apply to.
    /// </para>
    /// <para>
    /// A world's *typed* values hang off its list row rather than becoming a pane of their own: the
    /// detail column is a projection of whatever the WORLDS list has selected, so those values already
    /// belong to that row. Its two booleans cannot — a row carries at most one checkbox, and there are
    /// two of them — so they are the one thing on this screen that needs a pane, and it is appended
    /// (see <see cref="SecurityPane"/>) rather than slotted in beside the world it describes.
    /// </para>
    /// <para>
    /// A character's logging is two fields of the character's own row, drawn in the character form. It
    /// lives here because this is where the character it applies to is: F9 used to edit it on a
    /// screen of its own that resolved "the active character, or else the first one configured" and
    /// never said which — so the same screen edited a different character's log depending on what was
    /// connected. There is no Space-to-start checkbox because a character row draws none at all, and
    /// <see cref="LogFormat.None"/> already spells "off" as one of the format's own choices — one
    /// control over one stored value rather than two over one.
    /// </para>
    /// <para>
    /// Each list pane ends in its own buttons, because a button acts on the list it is drawn under and
    /// the cursor is already there. A button that would act on nothing is left out rather than drawn
    /// dead: a world with no characters offers <c>+ add character</c> and nothing else, so ⏎ never
    /// lands on a row that silently does nothing.
    /// </para>
    /// </summary>
    /// <param name="selectedSet">
    /// Which trigger set the third pane has selected — what <c>[[- del]]</c> would remove. It defaults to
    /// the first, the way every other pane's cursor starts on its first row, so a caller that only wants
    /// the navigable shape still gets the pane's real buttons.
    /// </param>
    internal static ScreenModel Model(
        IReadOnlyList<WorldDefinition> worlds,
        IReadOnlyList<TriggerSet> triggerSets,
        int selectedWorld,
        int selectedCharacter,
        int selectedSet = 0)
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
            // No toggle: a character row draws no checkbox (CharacterRow), and a bound control the
            // renderer never draws is a setting nobody can reach. There was one here — `autoLogin` — and
            // it is the reason two of this repo's own characters had a saved password that did nothing.
            // Every remaining ScreenToggle in this file is drawn where it is bound; see the security
            // rows and the trigger-set assignments, both of which render a real `[x]`.
            : ScreenModel.Rows(world.Characters, c => ScreenRow.Of(
                ScreenField.Name("name", () => c.Name, v => c.Name = v),
                ScreenField.Password("password", () => c.Password, v => c.Password = v),
                ScreenField.Defaulted(
                    "connect", () => c.ConnectString, v => c.ConnectString = v, ConnectStringTemplate.Default),
                ScreenField.Optional("on connect", () => c.OnConnect, v => c.OnConnect = v),
                ScreenField.Choice(
                    StartupLabel,
                    () => c.ConnectAtStartup ? StartupOn : StartupOff,
                    v => c.ConnectAtStartup = string.Equals(v, StartupOn, StringComparison.OrdinalIgnoreCase),
                    StartupChoices),
                ScreenField.Enumeration("log", () => c.Logging.Format, v => c.Logging.Format = v),
                ScreenField.Optional("log folder", () => c.Logging.Directory, v => c.Logging.Directory = v),
                ScreenField.Choice(
                    RestoreLabel,
                    () => c.Logging.RestoreLog ? RestoreOn : RestoreOff,
                    v => c.Logging.RestoreLog = string.Equals(v, RestoreOn, StringComparison.OrdinalIgnoreCase),
                    RestoreChoices)))
                .Concat(CharacterButtons(world, selectedCharacter))
                .ToArray();

        var securityRows = world is null ? Array.Empty<ScreenRow>() : SecurityRows(world);

        if (!HasCharacter(worlds, selectedWorld, selectedCharacter))
        {
            return new ScreenModel(worldRows, characterRows, Array.Empty<ScreenRow>(), securityRows)
            {
                Layout = PaneLayout,
            };
        }

        var character = worlds[selectedWorld].Characters[selectedCharacter];
        var setRows = ScreenModel.Rows(triggerSets, set => ScreenRow.Of(
            Assignment(character, set.Name),
            ScreenField.UniqueName(
                "set name",
                () => set.Name,
                value => RenameSet(worlds, set, value),
                triggerSets.Where(s => !ReferenceEquals(s, set)).Select(s => s.Name).ToArray()),
            ScreenField.Optional("description", () => set.Description, v => set.Description = v)))
            .Concat(SetButtons(worlds, triggerSets, selectedSet))
            .ToArray();

        return new ScreenModel(worldRows, characterRows, setRows, securityRows) { Layout = PaneLayout };
    }

    /// <summary>
    /// Where the four panes actually sit, which is not the order they are numbered in. The WORLDS list
    /// is the left column on its own; the detail column beside it runs security → characters →
    /// trigger sets down the screen, with the sets drawn in the editing band across the bottom.
    /// <para>
    /// The mismatch is deliberate and predates this: <see cref="SecurityPane"/> is <em>appended</em>,
    /// because a pane index is a cursor coordinate and slotting one in beside the world it describes
    /// would renumber every stop the screen and its tests navigate by. Numbering and drawing therefore
    /// disagree, and until the panes said where they were, ⇥ believed the numbering: it went WORLDS →
    /// CHARACTERS → TRIGGER SETS and then jumped back <em>up</em> the screen to the security
    /// checkboxes. Three hops forward and one backwards is what "the tabbing feels awkward" was.
    /// </para>
    /// <para>
    /// Stacking the three detail panes in one column is also what lets ↓ run from the TLS checkbox
    /// straight through the characters list and on into the trigger sets, which is how the character's
    /// own row is now arrived at rather than hunted for.
    /// </para>
    /// </summary>
    private static readonly ScreenPanePlace[] PaneLayout =
    {
        new(Column: 0, Row: 0), // WORLDS — the left column, alone
        new(Column: 1, Row: 1), // CHARACTERS — under the world's fields
        new(Column: 1, Row: 2), // TRIGGER SETS — the editing band at the foot of the screen
        new(Column: 1, Row: 0), // the world's security checkboxes, drawn inside the WORLD block
    };

    /// <summary>
    /// A character's opt-in to one set. Assignment is list membership rather than a boolean, and the
    /// character's own order decides which set wins a conflict (see
    /// <see cref="AppConfiguration.ResolveTriggerSets"/>) — so flipping it off <em>removes</em> the name
    /// and flipping it on appends, which is what keeps the priority the user can see the priority the
    /// resolver uses.
    /// </summary>
    private static ScreenToggle Assignment(CharacterDefinition character, string name) => new(
        () => character.TriggerSets.Contains(name),
        () =>
        {
            if (!character.TriggerSets.Remove(name))
            {
                character.TriggerSets.Add(name);
            }
        });

    /// <summary>
    /// Renames a set <em>and every reference to it</em>. This is the one rename on these screens that
    /// reaches outside the object it is on: a character selects automation by name, so a set renamed on
    /// its own would leave every character that used it pointing at a set that no longer exists — the
    /// automation would simply stop, silently, at the next connect. Each reference keeps its position in
    /// the character's list, because that order is what decides which set wins a conflict.
    /// <para>
    /// The references are found by the <em>old</em> name before anything is written, and undo comes for
    /// free: <see cref="ScreenField.Name"/>'s snapshot replays this same setter with the old name, which
    /// walks the references back the way it walked them here.
    /// </para>
    /// </summary>
    private static void RenameSet(IReadOnlyList<WorldDefinition> worlds, TriggerSet set, string name)
    {
        var renamed = name.Trim();
        TriggerSetReferences.Rename(TriggerSetReferences.Find(worlds, set.Name), renamed);
        set.Name = renamed;
    }

    /// <summary>
    /// The trigger-set list's buttons — the only place in the app that makes or unmakes a set. They live
    /// here because this pane is the only one that shows sets as things in their own right rather than
    /// as a column of somebody's rules: F2, F3, F4 and F6 each flatten one <em>kind</em> of the set's
    /// contents across every set, so a set holding none of that kind is invisible on all four, and a set
    /// you have just made holds nothing at all.
    /// <para>
    /// A new set is empty and named apart from its neighbours, because its name is a key rather than a
    /// label (<see cref="ScreenField.UniqueName"/>) and two called <c>New Set</c> could not both be
    /// assigned.
    /// </para>
    /// </summary>
    private static List<ScreenRow> SetButtons(
        IReadOnlyList<WorldDefinition> worlds, IReadOnlyList<TriggerSet> triggerSets, int selectedSet)
    {
        var rows = new List<ScreenRow>();

        // Arrays report IsReadOnly through IList<T>, and a renderer handed one (the unit tests, any
        // caller with a fixed projection) must not offer a button whose only effect would be to throw.
        if (triggerSets is not IList<TriggerSet> { IsReadOnly: false } list)
        {
            return rows;
        }

        rows.Add(ScreenRow.Of(ScreenButton.Add(
            AddSetLabel,
            list,
            () => new TriggerSet { Name = ScreenLists.Available(list.Select(s => s.Name), NewSetName) })));

        if (selectedSet >= 0 && selectedSet < list.Count)
        {
            rows.Add(ScreenRow.Of(RemoveSet(worlds, list, selectedSet)));
        }

        return rows;
    }

    /// <summary>
    /// Deleting a set, which is the destructive edit on these screens with the longest reach: the set
    /// goes, and so does every character's opt-in to it — a character left holding the name of a set
    /// that no longer exists would show an assignment that resolves to nothing.
    /// <para>
    /// It is a hand-built button rather than <see cref="ScreenButton.Remove{T}"/> because its undo is
    /// two restorations, not one: the set back at its index, and each stripped reference back at
    /// <em>its</em> index inside the character that held it. Restoring the set alone would be the
    /// quieter half of a change nobody agreed to — and the closing review offers exactly that undo, so
    /// this is the half of it that has to be complete.
    /// </para>
    /// <para>
    /// It is also why the review's question names more than a set's name: what the deletion costs is the
    /// rules inside it and the characters that used it, and neither is visible once it is gone.
    /// </para>
    /// </summary>
    private static ScreenButton RemoveSet(
        IReadOnlyList<WorldDefinition> worlds, IList<TriggerSet> sets, int index)
    {
        var set = sets[index];
        return new ScreenButton(
            ScreenButton.RemoveKeyLabel,
            () =>
            {
                var name = set.Name;
                var references = TriggerSetReferences.Find(worlds, name);
                sets.RemoveAt(index);
                TriggerSetReferences.Detach(references);

                return new ScreenPress(
                    () =>
                    {
                        sets.Insert(Math.Clamp(index, 0, sets.Count), set);
                        TriggerSetReferences.Reattach(references, name);
                    },
                    index);
            },
            ScreenButtonKind.Remove,
            set.Name,
            () => DescribeSet(worlds, set));
    }

    /// <summary>
    /// What deleting a trigger set would cost, in the words the closing review names it by: the set, the
    /// rules of every kind inside it, and the characters whose automation it is. Counted rather than
    /// listed past the name, because the interesting number is how much stops working.
    /// </summary>
    private static string DescribeSet(IReadOnlyList<WorldDefinition> worlds, TriggerSet set)
    {
        var rules = set.Triggers.Count + set.Aliases.Count + set.Macros.Count + set.Timers.Count;
        var users = TriggerSetReferences.Find(worlds, set.Name).Count;
        var parts = new List<string>();
        if (rules > 0)
        {
            parts.Add($"{rules.ToString(CultureInfo.InvariantCulture)} rule{Plural(rules)}");
        }

        if (users > 0)
        {
            parts.Add($"{users.ToString(CultureInfo.InvariantCulture)} character{Plural(users)} using it");
        }

        return parts.Count == 0
            ? $"trigger set {set.Name}"
            : $"trigger set {set.Name} and its {string.Join(", ", parts)}";
    }

    /// <summary>What deleting a world would cost: the world, and the characters that live on it.</summary>
    private static string DescribeWorld(WorldDefinition world) =>
        world.Characters.Count == 0
            ? $"world {world.Name}"
            : $"world {world.Name} and its {world.Characters.Count.ToString(CultureInfo.InvariantCulture)}"
                + $" character{Plural(world.Characters.Count)}";

    private static string Plural(int count) => count == 1 ? string.Empty : "s";

    /// <summary>
    /// The selected world's two security booleans, in the order the WORLD block draws them. They are
    /// checkboxes rather than one typed value because that is what they are — two independent flags,
    /// which no single field could offer without inventing a vocabulary for the four combinations of
    /// them — and they are two rows because a <see cref="ScreenRow"/> carries at most one checkbox.
    /// <para>
    /// Certificate validation is a plain <c>bool</c> here and is deliberately still bound in the
    /// direction config stores it: the checkbox reads <c>accept invalid certificates</c>, so the
    /// dangerous state is the *checked* one and the screen can paint it as such (see
    /// <see cref="CertificateRow"/>). A checkbox showing the inverse of its stored value would read
    /// more comfortably and would be one negation away from silently turning verification off.
    /// </para>
    /// </summary>
    private static ScreenRow[] SecurityRows(WorldDefinition world) => new[]
    {
        ScreenRow.Of(ScreenToggle.Bind(() => world.UseTls, v => world.UseTls = v)),
        ScreenRow.Of(ScreenToggle.Bind(
            () => world.AllowInvalidCertificates, v => world.AllowInvalidCertificates = v)),
    };

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
            var world = list[selectedWorld];
            rows.Add(ScreenRow.Of(ScreenButton.Remove(
                list, selectedWorld, target: world.Name, describe: () => DescribeWorld(world))));
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
                characters, selectedCharacter, target: source.Name, describe: () => $"character {source.Name}")));
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

    /// <param name="height">
    /// How many rows the column has, or 0 when the caller has none — the same contract
    /// <see cref="DetailColumn"/> keeps, and it is here for the same reason. Each world costs three rows,
    /// so at 100×24 two worlds and their buttons ran to twelve rows in ten and the two the column lost off
    /// the bottom were <c>[[+ world]]</c> and the row naming what Delete would take: a cursor stop that
    /// was never drawn, which is precisely the failure <see cref="ScreenChrome.Window"/> exists to stop.
    /// </param>
    internal static List<string> WorldsColumn(
        IReadOnlyList<WorldDefinition> worlds, int selectedWorld, ScreenFocus? focus = null, int height = 0)
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
                $"{marker} [{accentHex}]▚[/] {name}", cursor.IsOn(WorldsPane, i), LeftColumnWidth));
            left.Add($"    [{Label}]{Escape(world.Host)}:{world.Port.ToString(CultureInfo.InvariantCulture)}[/]");
            left.Add($"    [{Label}]{world.Characters.Count.ToString(CultureInfo.InvariantCulture)} chars[/]");
        }

        if (worlds.Count == 0)
        {
            left.Add($"[{Label}]no worlds[/]");
        }

        left.Add(string.Empty);
        left.AddRange(ScreenChrome.Buttons(
            WorldButtons(worlds, selectedWorld), cursor, WorldsPane, worlds.Count, LeftColumnWidth));

        // Compacted, then windowed — the same two steps the detail column takes, and it must be both:
        // dropping the blank separators buys back a row per world, and the window is what guarantees the
        // row the cursor is on is one of the rows drawn.
        return ScreenChrome.Window(ScreenChrome.Compact(left, height), height);
    }

    /// <param name="height">
    /// How many rows the column has, or 0 when the caller has none. This is the one pane on these
    /// screens that draws rows belonging to three of its four cursor panes, so on a short terminal it
    /// has to give its blanks up and then scroll — see <see cref="ScreenChrome.Compact"/> and
    /// <see cref="ScreenChrome.Window"/>. At 100×24 it ran to nineteen rows in twelve, and the twelve
    /// it drew stopped just above the CHARACTERS list, so every character row and every button under
    /// them was a cursor stop that had never been drawn.
    /// </param>
    internal static List<string> DetailColumn(
        IReadOnlyList<WorldDefinition> worlds,
        IReadOnlyList<TriggerSet> triggerSets,
        int selectedWorld,
        int selectedCharacter,
        string accent,
        ScreenFocus? focus = null,
        int height = 0)
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
        //
        // There is no title strip above them any more. It read
        // `Aetherfall  aetherfall.mux:4201  TLS on · UTF-8` — and every token of it was repeated in the
        // five rows immediately underneath, twice over for the address, which the WORLDS list beside it
        // also carries. A summary directly above the thing it summarises is not a summary; it is the
        // same row drawn again in a shape you cannot edit, and the two cells it cost were the two rows
        // the CHARACTERS list needed at 100×24.
        var right = new List<string>
        {
            $"[{accent}]├ WORLD[/]",
            WorldField("name", Field(
                $"[{Value}]{Escape(world.Name)}[/]", cursor, selectedWorld, WorldNameField)),
            WorldField("host", Field(
                $"[{Value}]{Escape(world.Host)}[/]", cursor, selectedWorld, HostField)),
            WorldField("port", Field(
                $"[{Value}]{world.Port.ToString(CultureInfo.InvariantCulture)}[/]",
                cursor,
                selectedWorld,
                PortField)),
        };

        right.AddRange(SecurityColumn(world, cursor));
        right.AddRange(new[]
        {
            WorldField("encoding", Field(
                EncodingDetail(world.Encoding), cursor, selectedWorld, EncodingField)),
            WorldField("keepalive", Field(
                world.KeepaliveSeconds > 0
                    ? $"[{Value}]{world.KeepaliveSeconds.ToString(CultureInfo.InvariantCulture)}s[/]"
                    : $"[{Label}]off[/]",
                cursor,
                selectedWorld,
                KeepaliveField)),
            string.Empty,
            $"[{accent}]├ CHARACTERS[/]   [{Label}]a character is a connection[/]",
        });

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
                    cursor.IsOn(CharactersPane, i),
                    DetailRowWidth));
            }
        }

        right.Add(string.Empty);
        right.AddRange(ScreenChrome.Buttons(
            CharacterButtons(world, selectedCharacter),
            cursor,
            CharactersPane,
            world.Characters.Count,
            DetailRowWidth));

        // Compacted, then windowed, then overlaid: the dropdown replaces rows by index, so anything that
        // moves a row has to happen before it is drawn or the list slides off the field it belongs to.
        return ScreenChrome.Choices(
            ScreenChrome.Window(ScreenChrome.Compact(right, height), height), cursor.Edit, DetailRowWidth);
    }

    /// <summary>
    /// The world's security block: two checkbox rows where a read-only <c>security  TLS on · certs
    /// strict</c> summary used to sit. The summary said everything and offered nothing — the two flags
    /// behind it had no UI at all — so it is replaced by the rows themselves rather than kept above
    /// them. The column's title strip used to repeat the answer a third time (<c>TLS on</c>) and has
    /// since gone with the rest of its duplications; the checkbox <em>is</em> the one-glance answer, and
    /// it is the only one that can also be pressed.
    /// <para>
    /// They keep the <c>security</c> label column so the block still reads as one setting with two
    /// switches, and so the checkboxes line up under the field wells above them rather than starting at
    /// the margin.
    /// </para>
    /// </summary>
    private static List<string> SecurityColumn(WorldDefinition world, ScreenFocus cursor) => new()
    {
        ScreenChrome.Cursor(
            WorldField("security", Checkbox("TLS", world.UseTls, "encrypt this connection")),
            cursor.IsOn(SecurityPane, 0),
            DetailRowWidth),
        ScreenChrome.Cursor(
            WorldField(string.Empty, CertificateRow(world)), cursor.IsOn(SecurityPane, 1), DetailRowWidth),
    };

    /// <summary>
    /// The certificate checkbox. It is the one row on these screens that can switch off a check the user
    /// is otherwise entitled to assume is running, so — unlike the encoding beside it — it does not
    /// stay quiet about it: checked *and* encrypting, it is drawn in <see cref="ScreenPalette.Warn"/>
    /// with the same <c>▲</c> a refused value gets, and says what the state actually costs rather than
    /// restating its own label.
    /// <para>
    /// With TLS off it is drawn plainly whatever it holds, and says so: nothing is being validated
    /// either way, and a warning that fires on a connection carrying no certificates would train the
    /// eye to ignore the one that matters.
    /// </para>
    /// </summary>
    private static string CertificateRow(WorldDefinition world)
    {
        const string label = "accept invalid certificates";

        if (!world.UseTls)
        {
            return Checkbox(label, world.AllowInvalidCertificates, "no effect until TLS is on");
        }

        return world.AllowInvalidCertificates
            ? $"[{Warn}][[x]] {Escape(label)}  ▲ anyone can impersonate this host[/]"
            : Checkbox(label, false, "certificates must be valid");
    }

    /// <summary>
    /// A checkbox row, checked in the accent and unchecked dim — the same shape F2's editor pane draws,
    /// so a checkbox means the same thing (Space presses it) wherever these screens put one.
    /// </summary>
    private static string Checkbox(string label, bool value, string hint)
    {
        var text = $"{Escape(label)}[{Label}] — {Escape(hint)}[/]";
        return value ? $"[{Accent}][[x]][/] [{Value}]{text}[/]" : $"[dim][[ ]][/] [{Label}]{text}[/]";
    }

    /// <summary>
    /// The character form — labels left-aligned with their values, one field per row. The editable ones
    /// are the character row's own fields (name, password, connect line, on-connect, <c>at start</c>,
    /// the two log values, then <c>restore</c>) and are the only eight drawn in a field well. The other
    /// two deliberately are not: <c>login</c> is <em>derived</em> from the fields above it and there is
    /// nothing there to set, and the session line is a report of what the connection is doing rather
    /// than a setting.
    /// <para>
    /// <b>A row here draws no checkbox at all</b> (<see cref="CharacterRow"/>), so every settable thing
    /// about a character is a field on this form and there is nothing Space can reach. That used not to
    /// be true in the model — the row bound a toggle over <c>autoLogin</c> which the renderer never drew
    /// — and the defect it caused is the one this whole change is about: the form said <c>auto-login
    /// no</c> with no well beside it, correctly meaning "not editable here", and there was no "here" to
    /// go to. A saved password sat inert behind a control that did not exist. The toggle is gone with the
    /// setting; do not put another one on this row without also drawing it.
    /// </para>
    /// <para>
    /// <c>at start</c> is therefore a field and must stay one. A closed two-value choice is the
    /// next-nearest control the screen already knows, and it lands the setting beside the rows it has to
    /// be told apart from. It is drawn directly above <c>login</c> on purpose: <c>at start</c> decides
    /// whether a connection is opened at launch, <c>login</c> reports what gets typed once one exists,
    /// and neither implies the other.
    /// </para>
    /// <para>
    /// The <b>password</b> was a seventh readout until it had somewhere to go. It was drawn without a
    /// well and labelled <c>keychain</c> — an affordance-free row advertising a credential store that
    /// does not exist anywhere in this codebase. It is now a real field, masked
    /// (<see cref="ScreenChrome.RestingMask"/>), and its note says what is actually true: the value is
    /// written to <c>secrets.json</c> in plaintext, and <em>not</em> into <c>config.json</c> (see
    /// <see cref="StorageNote"/>). The mask is about shoulders and screenshots, not about storage, and the
    /// note is what stops it being read as a claim about storage.
    /// </para>
    /// <para>
    /// The <b>connect line</b> is drawn here for the first time, because it is only now a thing worth
    /// looking at: it is a template (<see cref="ConnectStringTemplate"/>), it holds the two tokens by
    /// default, and a user who can see it can move their password out of it. While it was an
    /// interpolated string built in C#, the screen had nothing to show and no way to let anyone edit it,
    /// which is exactly why the plaintext-in-the-connect-string workaround was the only option on offer.
    /// </para>
    /// <para>
    /// The log rows are here, under a heading that names the character, because logging <em>is</em> per
    /// character: <c>LoggingSettings</c> hangs off <see cref="CharacterDefinition"/>. On its own screen
    /// it had to guess whose settings to show and said nothing about the answer; here the question
    /// cannot come up, and the row says <c>this character only</c> anyway for the reader arriving from
    /// the F9 it used to live on.
    /// </para>
    /// </summary>
    internal static List<string> FormColumn(
        CharacterDefinition character, string accent, ScreenFocus? focus = null, int selectedCharacter = -1)
    {
        var cursor = focus ?? ScreenFocus.None;
        var form = new List<string>
        {
            $"[bold {accent}]└ CHARACTER · {Escape(character.Name)}[/]",
            FormDoor(cursor, selectedCharacter),
            CharField("name", Field(
                $"[{Value}]{Escape(character.Name)}[/]",
                cursor,
                selectedCharacter,
                CharacterNameField,
                pane: CharactersPane)),
            CharField("password", Field(
                character.Password is { Length: > 0 }
                    ? ScreenChrome.RestingMask()
                    : $"[{Label}]{NoPassword}[/]",
                cursor,
                selectedCharacter,
                PasswordField,
                pane: CharactersPane)),
            CharField(string.Empty, $"[{Label}]{StorageNote}[/]"),
            CharField("connect", Field(
                $"[{Value}]{Escape(character.ConnectString ?? ConnectStringTemplate.Default)}[/]",
                cursor,
                selectedCharacter,
                ConnectStringField,
                pane: CharactersPane)),
            CharField("on connect", Field(
                $"[{Value}]{Escape(character.OnConnect ?? "—")}[/]",
                cursor,
                selectedCharacter,
                OnConnectField,
                pane: CharactersPane)),
            CharField(StartupLabel, Field(
                StartupDetail(character.ConnectAtStartup),
                cursor,
                selectedCharacter,
                StartupField,
                pane: CharactersPane)),
            CharField("login", LoginDetail(character)),
            CharField("session", ScreenChrome.ReadOnly("offline")),
            CharField("log", Field(
                $"[{Value}]{character.Logging.Format.ToString()}[/]",
                cursor,
                selectedCharacter,
                LogFormatField,
                pane: CharactersPane) + $" [{Label}]— this character only[/]"),
            CharField("log folder", Field(
                $"[{Value}]{Escape(character.Logging.Directory ?? DefaultDirectory)}[/]",
                cursor,
                selectedCharacter,
                LogDirectoryField,
                pane: CharactersPane)),
            CharField(RestoreLabel, Field(
                character.Logging.RestoreLog
                    ? $"[{Value}]{RestoreOn}[/]"
                    : $"[{Label}]{RestoreOff}[/]",
                cursor,
                selectedCharacter,
                RestoreLogField,
                pane: CharactersPane)),
            CharField(string.Empty, $"[{Label}]{RestoreNote}[/]"),
        };

        // The log format and `restore` both sit near the foot of this block, so their lists are drawn
        // upward — the one place on these screens where a downward list would have nowhere to go, and
        // Choices picks the direction from the room it has rather than being told. The block's height
        // is a grid row in WorldsScreenView, which is the other half of why a list overlays rather than
        // pushes: growing one would resize the pane and shove the whole screen about.
        return ScreenChrome.Choices(form, cursor.Edit, CharDetailColumnWidth);
    }

    /// <summary>
    /// What an unset log directory reads as. The field itself holds null — the logger picks a
    /// per-session folder under the config directory — so the row names the state rather than showing
    /// an empty well that would look like a value someone had blanked.
    /// </summary>
    internal const string DefaultDirectory = "(default)";

    /// <summary>
    /// What an unset password reads as, so the well is a visible box one goes to rather than a one-cell
    /// tint. It follows <see cref="DefaultDirectory"/>'s precedent, and says "nothing here" rather than
    /// drawing a mask over nothing — dots standing for no password would claim one.
    /// </summary>
    internal const string NoPassword = "(not set)";

    /// <summary>
    /// What the password row says about where the value goes: into <c>secrets.json</c>, as plaintext.
    /// <see cref="CharacterDefinition.Password"/> is saved — just not into <c>config.json</c>, which carries
    /// only a meaningless GUID (<see cref="CharacterDefinition.PasswordRef"/>). This row is the only place a
    /// user could learn any of that.
    /// <para>
    /// It has said four different things, and the sequence is worth keeping legible because each was true of
    /// the design it shipped with. It said <c>keychain</c> — a credential store that exists nowhere in this
    /// codebase, which is the exact class of claim these screens' tests are written to prevent. It said
    /// <c>this session only — never saved</c>, honest while the field was <c>[JsonIgnore]</c> and a lie the
    /// moment passwords were persisted. It briefly said <c>saved in config.json, plain text</c>, which was
    /// honest about a design that put the secret in the file people paste. It now names the file the secret
    /// is actually in.
    /// </para>
    /// <para>
    /// <b>Plainly, not glossed.</b> "Encrypted" would be false, "stored securely" would be a gloss over a
    /// plaintext file, and saying only <c>saved</c> would let a reader supply the reassuring half themselves.
    /// Naming the file is what makes the note actionable — a user who wants to look after their credentials,
    /// exclude them from a backup or check what is in them now knows what to open. The two things it does not
    /// have room for are the owner-only mode and the fact that <c>config.json</c> is consequently safe to
    /// share; both are in <c>docs/design/README.md</c> and in <c>--help</c>, and neither is something a reader
    /// of this row has to act on.
    /// </para>
    /// <para>
    /// It takes a row of its own, with a blank label, the way the certificate checkbox hangs off the
    /// <c>security</c> row above it. Beside the value it would have had to share a 48-cell panel
    /// (<see cref="WorldsScreenView"/>) with a field whose drawn width is the buffer's own — so it wrapped
    /// the moment a password was set, and a wrapped row costs the form a line it was not measured for and
    /// pushed <c>log folder</c> out of the band. On its own row it cannot collide with a value of any
    /// length, and it is drawn while the field is open as well as at rest, which is when it is most worth
    /// reading. <c>CharField</c> spends 14 of the panel's 47 usable cells on the indent and the label
    /// column, so this string has 33 to fit in and
    /// <c>ScreenPasswordFieldTests.EveryFormRowFitsThePanel…</c> is what stops the next edit forgetting it.
    /// </para>
    /// </summary>
    internal const string StorageNote = "saved in secrets.json, plaintext";

    /// <summary>What the CHARACTER form says while the cursor is somewhere else on the screen.</summary>
    internal const string FormClosed = "⏎ on the character in the list above";

    /// <summary>And while it is on that character's own row, one keystroke from opening these.</summary>
    internal const string FormOpen = "⏎ opens these";

    /// <summary>
    /// The one line on this screen that says how to get into it. The CHARACTER form draws four field
    /// wells — the affordance these screens use to mean "the keyboard can change this here" — but the
    /// cursor stop that opens them is the character's row in the CHARACTERS list, a column and a band
    /// away. So the wells were honest about being editable and silent about being editable
    /// <em>from somewhere else</em>, and the way in was reachable, correct and unguessable: the row you
    /// arrive on by ⇥ is a bare selector with no well of its own, which by this project's own rule reads
    /// as "nothing to type into here".
    /// <para>
    /// It is derived from the cursor rather than written once, so it names the key at the moment the key
    /// would work: <c>⏎ opens these</c> while the cursor is on that character's row, and where to go
    /// otherwise. Mid-edit it says nothing at all — the header hints have already swapped wholesale to
    /// <c>⏎ commit · Esc revert</c>, and a second, staler claim beside them would be the disagreement
    /// those hints exist to prevent.
    /// </para>
    /// <para>
    /// It takes the blank row under the heading rather than sitting beside it, so it costs no row and
    /// cannot be pushed off the form's 48-cell column by a long character name — the heading already
    /// spends that width on the name itself. It still reads as a separator when it is empty.
    /// </para>
    /// </summary>
    private static string FormDoor(ScreenFocus cursor, int selectedCharacter)
    {
        if (cursor.IsEditing)
        {
            return string.Empty;
        }

        return cursor.IsOn(CharactersPane, selectedCharacter)
            ? $"  [{Accent}]{FormOpen}[/]"
            : $"  [{Label}]{FormClosed}[/]";
    }

    /// <summary>Draws a value as a field, showing the buffer and caret when its edit is the open one.</summary>
    private static string Field(string display, ScreenFocus cursor, int index, int field, int pane = 0) =>
        ScreenChrome.Field(display, cursor.EditOn(pane, index, field));

    /// <summary>
    /// The trigger-set list: which sets exist, what each is called and for, how much it holds, and
    /// whether this character has opted into it. It is the app's only view of sets as objects — every
    /// other screen shows one <em>kind</em> of a set's contents flattened across all of them — so it is
    /// also where sets are made, renamed and unmade, and the only place an empty one can be seen at all.
    /// <para>
    /// The name and description are welled because ⏎ opens them here; the assignment stays a checkbox,
    /// which is the affordance for the key that presses it.
    /// </para>
    /// </summary>
    internal static List<string> TriggersColumn(
        CharacterDefinition character,
        IReadOnlyList<TriggerSet> triggerSets,
        string accent,
        ScreenFocus? focus = null,
        int selectedSet = 0,
        IReadOnlyList<WorldDefinition>? worlds = null)
    {
        var cursor = focus ?? ScreenFocus.None;

        // Padded to shared widths so the wells line up into columns and the inventories at the end of
        // each row can be read down. Measured over the drawn text, since a name may carry brackets.
        var nameWidth = Width(triggerSets, s => "▪ " + Escape(s.Name));
        var noteWidth = Width(triggerSets, s => Escape(s.Description ?? NoDescription));

        var rows = new List<string>(triggerSets.Count);
        foreach (var set in triggerSets)
        {
            var assigned = character.TriggerSets.Contains(set.Name);
            var box = assigned ? $"[{accent}][[x]][/]" : $"[{Label}][[ ]][/]";
            var nameColor = assigned ? Value : Label;
            var index = rows.Count;

            // The bullet lives inside the well with the name rather than beside it: the well is the box
            // the value is typed in, and a bullet floating outside it would read as a second column.
            var name = ScreenChrome.Field(
                PadVisible($"[{nameColor}]▪ {Escape(set.Name)}[/]", nameWidth),
                cursor.EditOn(TriggerSetsPane, index, SetNameField));
            var note = ScreenChrome.Field(
                PadVisible($"[{Label}]{Escape(set.Description ?? NoDescription)}[/]", noteWidth),
                cursor.EditOn(TriggerSetsPane, index, SetDescriptionField));

            rows.Add($"{box} {name} {note} [{Label}]{Inventory(set)}[/]");
        }

        // The checklist sits in an auto-width column (see WorldsScreenView), so a cursor bar sized to
        // one row would widen the block and shunt it sideways as the cursor moved. Sizing every bar to
        // the widest row keeps the column's measured width constant whatever is focused.
        var buttons = ScreenChrome.Buttons(
            SetButtons(worlds ?? Array.Empty<WorldDefinition>(), triggerSets, selectedSet),
            cursor,
            TriggerSetsPane,
            triggerSets.Count,
            0);
        var barWidth = rows.Count == 0 ? 0 : rows.Max(VisibleLength);

        var list = new List<string> { $"[{Label}]trigger sets[/]", string.Empty };
        for (var i = 0; i < rows.Count; i++)
        {
            list.Add(ScreenChrome.Cursor(rows[i], cursor.IsOn(TriggerSetsPane, i), barWidth));
        }

        if (triggerSets.Count == 0)
        {
            list.Add($"[{Label}]no trigger sets — nothing to assign yet.[/]");
        }

        list.Add(string.Empty);
        list.AddRange(buttons);
        return list;
    }

    /// <summary>
    /// What a set holds, counted across everything it can hold rather than only its triggers. The count
    /// is the one thing on this row that says whether a set is doing anything, and a set carrying two
    /// timers and no triggers reading <c>0 rules</c> would be exactly the wrong answer.
    /// </summary>
    private static string Inventory(TriggerSet set)
    {
        var count = set.Triggers.Count + set.Aliases.Count + set.Macros.Count + set.Timers.Count;
        return count == 0
            ? "empty"
            : $"{count.ToString(CultureInfo.InvariantCulture)} rules";
    }

    /// <summary>The widest of a projection over the sets, or zero when there are none.</summary>
    private static int Width(IReadOnlyList<TriggerSet> sets, Func<TriggerSet, string> part) =>
        sets.Count == 0 ? 0 : sets.Max(s => VisibleLength(part(s)));

    /// <summary>
    /// One row of the CHARACTERS list: which character, and nothing else. It is a <em>selector</em> —
    /// the row you move the cursor onto to bring a character up in the CHARACTER form below — and it
    /// used to be a four-column table (<c>name  state  login  trigger sets</c>) whose other three
    /// columns were all drawn again, on the same screen, at the same time: <c>session</c> and
    /// <c>login</c> are rows of the form directly underneath, and the sets are the pane beside it,
    /// with checkboxes, descriptions and counts. A list that restates the detail pane is a list you have
    /// to read to discover it says nothing new — and its column header cost a row that the list itself
    /// needed on a short screen.
    /// <para>
    /// So: the list says which characters there are and which one is selected, and the form owns
    /// everything about the one that is.
    /// </para>
    /// </summary>
    private static string CharacterRow(CharacterDefinition character, bool selected)
    {
        var marker = selected ? $"[bold {Accent}]▸[/]" : " ";
        return $"{marker} [{(selected ? "bold " : string.Empty)}{Value}]{Escape(character.Name)}[/]";
    }

    private static string WorldField(string label, string value) =>
        $"  [{Label}]{label.PadLeft(WorldLabelWidth)}[/]  {value}";

    /// <summary>
    /// The encoding cell: <c>auto</c> reads as the unremarkable default it is (dimmed, like keepalive's
    /// <c>off</c> directly below it), and anything else is named <em>override</em>, because the choice
    /// list gives no other way to tell that picking one stops the client believing the server.
    /// </summary>
    /// <summary>
    /// The <c>at start</c> cell: unmarked reads as the unremarkable default it is (muted, like the
    /// <c>keepalive</c> row's <c>off</c> and the encoding's <c>auto</c>), and marked is drawn in the
    /// value ink because it is a thing this client will go and do on its own.
    /// </summary>
    internal static string StartupDetail(bool connectAtStartup) =>
        connectAtStartup ? $"[{Value}]{StartupOn}[/]" : $"[{Label}]{StartupOff}[/]";

    /// <summary>
    /// The <c>login</c> cell: what this character will actually type when it connects, read off
    /// <see cref="CharacterDefinition.Login"/> rather than off a setting.
    /// <para>
    /// <b>This row is the whole reason the screen changed.</b> It replaced an <c>auto-login</c> readout
    /// that said <c>yes</c> or <c>no</c> about a stored boolean — and a character could sit there with a
    /// saved password, that flag at its default, and a row cheerfully reading <c>no</c> that never
    /// explained it was the reason the password did nothing. Two of this repo's own characters were in
    /// exactly that state. The flag is gone; this row reports the derived answer, so there is no longer a
    /// setting the screen can agree with while the client disagrees.
    /// </para>
    /// <para>
    /// <see cref="LoginPlan.PasswordUnused"/> is drawn in <see cref="ScreenPalette.Warn"/>, the ink the
    /// only other misconfiguration on this screen uses (a world trusting invalid certificates). It is the
    /// one state here where the user has done something that cannot work: the password field above is
    /// filled in and the connect line beside it has nowhere to put it.
    /// </para>
    /// </summary>
    internal static string LoginDetail(CharacterDefinition character) => character.Login() switch
    {
        LoginPlan.WithPassword => ScreenChrome.ReadOnly(LoginWithPassword),
        LoginPlan.WithoutPassword => ScreenChrome.ReadOnly(LoginWithoutPassword),
        LoginPlan.PasswordUnused => $"[{Warn}]▲ {Escape(LoginPasswordUnused)}[/]",
        _ => ScreenChrome.ReadOnly(LoginNothing),
    };

    /// <summary>Sends the connect line with the saved password in it — the ordinary configured case.</summary>
    internal const string LoginWithPassword = "sends connect + password";

    /// <summary>Sends a connect line that involves no password, because none is saved.</summary>
    internal const string LoginWithoutPassword = "sends the connect line";

    /// <summary>
    /// Sends nothing. It names the two fields that would change that, because "nothing" on its own is
    /// indistinguishable from a row that is broken — and this is the state a user arrives in when they
    /// expected a login and did not get one.
    /// </summary>
    internal const string LoginNothing = "nothing — set a password";

    /// <summary>
    /// A saved password with no <c>%PASSWORD%</c> in the connect line. Names the token, because the fix
    /// is to put it back and the row is two lines under the field it belongs in.
    /// </summary>
    internal const string LoginPasswordUnused = "password unused — no %PASSWORD%";

    internal static string EncodingDetail(string? encoding) =>
        TelnetSessionOptions.ResolveEncoding(encoding) is null
            ? $"[{Label}]{TelnetSessionOptions.AutoEncodingName}[/]"
            : $"[{Value}]{Escape(encoding!)}[/] [{Label}]override[/]";

    private static string CharField(string label, string value) =>
        $"  [{Label}]{label.PadRight(CharLabelWidth)}[/]  {value}";

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
