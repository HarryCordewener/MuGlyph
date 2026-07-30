using System.Globalization;
using System.Text.RegularExpressions;
using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Text;

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
/// <param name="Choices">
/// The values the open field knows about, or null when it knows none. Carried on the edit rather than
/// looked up again per renderer, because every screen now <em>draws</em> them
/// (<see cref="ScreenChrome.Choices"/>) as well as stepping through them, and a list the chrome
/// re-derived could disagree with the list ↑↓ actually walks.
/// </param>
/// <param name="ClosedChoices">
/// Whether <paramref name="Choices"/> is the permitted set rather than a shortlist of suggestions —
/// the difference between a log format (only these four values exist) and a window name (these are
/// the windows in use; typing a fifth is how the fifth comes into being). The chrome says which,
/// because a list drawn the same way for both would imply a closed set where anything is legal.
/// </param>
/// <param name="Capture">
/// Whether the open field takes its value from the <em>next keystroke</em> rather than from a typed
/// buffer — F4's key binding is the only one. It changes what the chrome draws (a prompt, not a caret)
/// and what the hints promise (no ⏎, no ⇥: every key but Esc is a candidate), so it is carried on the
/// edit rather than re-derived, the way <paramref name="ClosedChoices"/> is.
/// </param>
/// <param name="Masked">
/// Whether the buffer is a secret, and so must be drawn as dots rather than as itself — a character's
/// password is the only one (<see cref="ScreenField.Password"/>). <see cref="Text"/> still carries the
/// real value, because that is what ⏎ commits and what Backspace has to edit; the masking is the
/// chrome's, applied at the last moment before markup (see <c>ScreenChrome.Field</c>), so the plaintext
/// never exists in a rendered frame that a snapshot, a screenshot or a shoulder could read.
/// </param>
internal readonly record struct ScreenFieldEdit(
    int Field,
    string Text,
    int Caret,
    string? Error,
    int RowFields = 1,
    IReadOnlyList<string>? Choices = null,
    bool ClosedChoices = false,
    bool Capture = false,
    bool Masked = false)
{
    /// <summary>Whether the open field knows any values at all, whatever the buffer currently is.</summary>
    internal bool HasChoices => Choices is { Count: > 0 };

    /// <summary>
    /// The choices the buffer currently narrows to — what the dropdown lists and what ↑↓ step through,
    /// which are deliberately the same list (see <see cref="ScreenField.Matching"/>). It can be empty
    /// while <see cref="HasChoices"/> is true: a buffer naming something new matches nothing, which is
    /// a legal state on an open field and the reason the chrome reads them apart.
    /// </summary>
    internal IReadOnlyList<string> VisibleChoices => ScreenField.Matching(Choices, Text);
}

/// <summary>
/// An editable value on a settings row: how to read it as text, whether a typed string is a legal
/// value for it, and how to write it.
/// <para>
/// There is nothing here about putting the old value back. A field's escape hatch is its
/// <em>buffer</em>: what is being typed lives in <see cref="SettingsSession"/> until ⏎ accepts it, and
/// Esc abandons the buffer without config ever having seen it. Past that point the value is committed
/// and is kept — see <see cref="ScreenEdits"/> for the scope rule — so no field is ever asked to
/// restore itself.
/// </para>
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
/// <param name="Choices">The values the field knows about, else null.</param>
/// <param name="ClosedChoices">
/// Whether <paramref name="Choices"/> is the <em>permitted</em> set. A closed field refuses anything
/// outside it (<see cref="Choice"/>, <see cref="Enumeration{TEnum}"/>); an open one merely suggests
/// (<see cref="WindowName"/>, <see cref="Colour"/>, <see cref="Template"/>), and its validator says so
/// independently. It is carried here rather than inferred, because the chrome draws the two lists
/// differently and a renderer guessing from the field's shape would eventually guess wrong.
/// </param>
/// <param name="Capture">
/// Whether the value is taken from the next keystroke instead of typed. See <see cref="Key"/>: it is the
/// one field on these screens whose vocabulary is the keyboard itself, so a text buffer could only ever
/// be a place to mis-spell a key name.
/// </param>
/// <param name="Masked">
/// Whether the value is a secret. It changes nothing about how the field behaves — it is typed,
/// validated and committed like any other — and everything about how it is <em>drawn</em>: the chrome
/// renders one dot per character instead of the text. It is carried on the field so the renderer cannot
/// forget, the same way <paramref name="Capture"/> is. See <see cref="Password"/>.
/// </param>
/// <param name="Follow">
/// Where the row this field belongs to has ended up, asked <em>after</em> a commit, or null for the
/// fields that leave their row where it was — which is nearly all of them. It exists for
/// <see cref="ScreenLists.Owner{T}"/>: writing that field moves the item into another
/// <see cref="SharpMUTerm.Core.Configuration.TriggerSet"/>'s list, and the panes those four screens
/// draw are flattened across every set, so the row genuinely changes position under the cursor. Without
/// it the cursor would be left pointing at whatever slid into the vacated row — and the next ⇥ would
/// edit that instead, which is the one thing a move must not do.
/// <para>
/// It is the field-side counterpart of <see cref="ScreenPress.Select"/>, and for the same reason: only
/// the thing that performed the change knows where the row went.
/// </para>
/// </param>
internal readonly record struct ScreenField(
    string Label,
    Func<string> Get,
    Func<string, string?> Validate,
    Action<string> Set,
    IReadOnlyList<string>? Choices = null,
    bool ClosedChoices = false,
    bool Capture = false,
    bool Masked = false,
    Func<int>? Follow = null)
{
    /// <summary>Longest rejection message kept; regex parser errors run to several lines otherwise.</summary>
    private const int MaxErrorLength = 44;

    /// <summary>
    /// The choices a buffer narrows to: everything, when the buffer is empty or already <em>names</em>
    /// one of them, and otherwise every choice containing it, case-insensitively, in the field's own
    /// order.
    /// <para>
    /// The exception for an exact name is what makes this usable rather than merely correct. A field
    /// opens on its committed value, so a plain filter would collapse the list to the one entry already
    /// selected the instant it was drawn — the dropdown would never show you the alternatives it exists
    /// to show. A buffer that names a choice is a <em>selection</em>, so the whole list stays up with
    /// that entry marked; a buffer that doesn't is a search, so the list narrows to what it matched.
    /// </para>
    /// <para>
    /// Substring rather than prefix: colour names are remembered by their middles as often as their
    /// starts (<c>gre</c> finding <c>green</c> and <c>grey</c> is the point), and the list is short
    /// enough that a loose match never floods it.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> Matching(IReadOnlyList<string>? choices, string buffer)
    {
        if (choices is not { Count: > 0 })
        {
            return Array.Empty<string>();
        }

        var trimmed = buffer.Trim();
        if (trimmed.Length == 0 || IndexOf(choices, trimmed) >= 0)
        {
            return choices;
        }

        var matched = new List<string>();
        foreach (var choice in choices)
        {
            if (choice.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                matched.Add(choice);
            }
        }

        return matched;
    }

    /// <summary>Where a buffer sits in a list of choices, matched by name, or -1 when it names none.</summary>
    internal static int IndexOf(IReadOnlyList<string> choices, string buffer)
    {
        ArgumentNullException.ThrowIfNull(choices);

        var trimmed = buffer.Trim();
        for (var i = 0; i < choices.Count; i++)
        {
            if (string.Equals(choices[i], trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The choice <paramref name="direction"/> steps from <paramref name="current"/>, wrapping at both
    /// ends — how ↑↓ move through the drawn list. Null when there is nothing to step to.
    /// <para>
    /// It walks <see cref="Matching"/> rather than every choice, so ↑↓ move through exactly the entries
    /// the dropdown is showing: typing <c>pa</c> narrows the list to <c>pages</c> and ↓ takes it. A
    /// buffer that matched nothing has an empty list and returns null — the keystroke is swallowed
    /// rather than overwriting a name being typed for the first time, which on an open field is the
    /// whole reason the field is open.
    /// </para>
    /// </summary>
    internal string? Cycle(string current, int direction)
    {
        var visible = Matching(Choices, current);
        if (visible.Count == 0)
        {
            return null;
        }

        var at = IndexOf(visible, current);
        var next = at < 0 ? (direction > 0 ? 0 : visible.Count - 1) : at + direction;
        return visible[((next % visible.Count) + visible.Count) % visible.Count];
    }

    /// <summary>
    /// Free text that may not be blank — a name, a host, a dictionary. Trimmed on commit.
    /// <para>
    /// <paramref name="known"/> is offered the way <see cref="WindowName"/> offers the spawn windows:
    /// values worth naming, not the permitted set. A dictionary is whichever locale the speller has
    /// installed and a newline key is whatever chord the terminal delivers, so neither can be closed —
    /// but neither should be typed blind either, which is exactly what a suggestion list is for.
    /// </para>
    /// </summary>
    internal static ScreenField Text(
        string label, Func<string> get, Action<string> set, IReadOnlyList<string>? known = null)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);

        return new ScreenField(
            label,
            get,
            value => string.IsNullOrWhiteSpace(value) ? $"{label} cannot be empty" : null,
            value => set(value.Trim()),
            known is { Count: > 0 } ? known : null);
    }

    /// <summary>
    /// What an item is called: the primary identifier every list screen draws in its leftmost column.
    /// Free text, trimmed, rejected when blank or when it carries control characters — a name is drawn
    /// into a single row of a fixed-width list, and a tab or a newline inside one would break the
    /// column it is drawn in, exactly as it would in a tab title (see <see cref="WindowName"/>).
    /// <para>
    /// Deliberately <em>not</em> unique: nothing keys off these names (the engines match on patterns and
    /// are keyed by <see cref="SharpMUTerm.Core.Automation.Macro.Key"/>), and two sets may each
    /// legitimately hold a rule called <c>Tell</c>. Only the <c>duplicate</c> buttons name their copies
    /// apart, and only so a fresh copy is findable in the list it lands in.
    /// </para>
    /// </summary>
    internal static ScreenField Name(string label, Func<string> get, Action<string> set)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);

        return new ScreenField(
            label,
            get,
            value => string.IsNullOrWhiteSpace(value)
                ? $"{label} cannot be empty"
                : value.Any(char.IsControl) ? $"{label} cannot contain control characters" : null,
            value => set(value.Trim()));
    }

    /// <summary>
    /// A name that is also a <em>key</em>: everything <see cref="Name"/> refuses, plus any name already
    /// taken by one of its siblings. A <see cref="SharpMUTerm.Core.Configuration.TriggerSet"/>'s name is
    /// the only one of these on the settings screens, and it is the exception that proves
    /// <see cref="Name"/>'s rule — two rules called <c>Tell</c> are merely confusing, but two sets called
    /// <c>Comms</c> are broken: a character opts into automation <em>by name</em>
    /// (<see cref="SharpMUTerm.Core.Configuration.CharacterDefinition.TriggerSets"/> is a list of
    /// strings), and <see cref="SharpMUTerm.Core.Configuration.AppConfiguration.ResolveTriggerSets"/>
    /// takes the first match — so the second set of a colliding pair can never be assigned to anything.
    /// <para>
    /// Comparison is case-insensitive, matching the resolver: <c>comms</c> and <c>Comms</c> are one name
    /// as far as an assignment is concerned, so they must be one name here too.
    /// </para>
    /// </summary>
    /// <param name="taken">The names of the item's siblings — its own current name excluded.</param>
    internal static ScreenField UniqueName(
        string label, Func<string> get, Action<string> set, IReadOnlyList<string> taken)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(taken);

        var named = Name(label, get, set);
        return named with
        {
            Validate = value => named.Validate(value)
                ?? (taken.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase)
                    ? $"another {label} is already called that"
                    : null),
        };
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
            value => set(string.IsNullOrWhiteSpace(value) ? null : value.Trim()));
    }

    /// <summary>
    /// A secret: typed like any other value, drawn as dots (see <see cref="Masked"/>). A character's
    /// login password is the only one, and it is why the field kind exists — the alternative on offer
    /// before this was for the user to write the password into the connect string, an ordinary text field
    /// that draws its value in the clear.
    /// <para>
    /// <b>What the mask is and is not.</b> It keeps the value out of rendered markup, which is what a
    /// snapshot writes to a file and a screenshot publishes — and a screenshot with a live password in it is
    /// the leak that prompted the storage design behind this field. It says nothing about storage: the
    /// committed value is written to <c>secrets.json</c> in plaintext (see
    /// <see cref="SharpMUTerm.Core.Configuration.SecretsStore"/>), and the row carries a note saying so,
    /// because a masked field is exactly the thing a reader would otherwise assume was encrypted.
    /// </para>
    /// <para>
    /// The buffer opens on the <em>real</em> value rather than on a blank, so an existing password can be
    /// corrected a character at a time instead of retyped from scratch; nothing is revealed by that,
    /// because the value's only route to a frame is through the mask.
    /// </para>
    /// <para>
    /// Blank is stored as null — that is "no password", not "a password of length zero" — but the value
    /// is otherwise committed <b>untrimmed</b>, which is the one place this field departs from every
    /// other text field on these screens. A trimmed name is a tidier name; a trimmed secret is a
    /// different secret, and it would fail at the server with the field on screen showing the value the
    /// user typed.
    /// </para>
    /// <para>
    /// Control characters are refused, for the reason <see cref="Template"/> refuses them and one more:
    /// the value goes out on a single login line, so an embedded newline would split it into two
    /// commands — the second of which would be the rest of the password, sent to a server that is no
    /// longer reading a password.
    /// </para>
    /// </summary>
    internal static ScreenField Password(string label, Func<string?> get, Action<string?> set)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);

        return new ScreenField(
            label,
            () => get() ?? string.Empty,
            value => value.Any(char.IsControl) ? $"{label} cannot contain control characters" : null,
            value => set(value.Length == 0 ? null : value),
            Masked: true);
    }

    /// <summary>
    /// A value with a <em>default</em> behind it: held as null when it is unset, but read, drawn and
    /// opened as <paramref name="fallback"/> — a character's connect string, whose default is the
    /// literal template <c>connect %CHARACTER% %PASSWORD%</c>.
    /// <para>
    /// It is not <see cref="Optional"/>, whose null shows as an empty well: a login line the user has
    /// never touched still <em>has</em> a value, and an empty box would say the opposite of the truth
    /// about what auto-login is about to send. Opening the field on the effective line is also what
    /// makes it editable in practice — the syntax is discoverable by looking at the thing that already
    /// works rather than by being told about it.
    /// </para>
    /// <para>
    /// Committing the fallback back verbatim stores null again, so "unset" has exactly one spelling in
    /// config and a later change to the default still reaches everyone who never overrode it. Blanking
    /// the field is the deliberate way back to it, and <paramref name="fallback"/> is offered as the
    /// single ↑↓ suggestion so there is a key that restores it without retyping.
    /// </para>
    /// </summary>
    internal static ScreenField Defaulted(
        string label, Func<string?> get, Action<string?> set, string fallback)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(fallback);

        return new ScreenField(
            label,
            () => get() ?? fallback,
            value => value.Any(char.IsControl) ? $"{label} cannot contain control characters" : null,
            value =>
            {
                var trimmed = value.Trim();
                set(trimmed.Length == 0 || string.Equals(trimmed, fallback, StringComparison.Ordinal)
                    ? null
                    : trimmed);
            },
            new[] { fallback });
    }

    /// <summary>
    /// An action that is <em>off</em> when it is blank — a trigger's rewrite template, the command it
    /// answers with, the script it calls. Blank is stored as null rather than as <c>""</c>, so "unset"
    /// and "set to nothing" cannot drift apart in config the way two spellings of the same state
    /// always do; the screens draw the null state in words.
    /// <para>
    /// Refused when it carries control characters. All three of these values are typed on one row and
    /// drawn on one row, and a newline inside one would break both — a rewrite would smuggle a second
    /// output line past the line model, and a response would smuggle a second command past the server.
    /// </para>
    /// <para>
    /// <paramref name="known"/> is offered as ↑↓ suggestions exactly the way <see cref="WindowName"/>
    /// offers the spawn windows already in use: values seen elsewhere in the configuration, not a
    /// closed list. Free text is the point — a script callback naming a function nothing calls yet is
    /// how the first rule that calls it gets written.
    /// </para>
    /// </summary>
    internal static ScreenField Template(
        string label, Func<string?> get, Action<string?> set, IReadOnlyList<string>? known = null)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);

        return new ScreenField(
            label,
            () => get() ?? string.Empty,
            value => value.Any(char.IsControl) ? $"{label} cannot contain control characters" : null,
            value => set(string.IsNullOrWhiteSpace(value) ? null : value.Trim()),
            known is { Count: > 0 } ? known : null);
    }

    /// <summary>
    /// A <c>[Flags]</c> enumeration: several independent booleans, written as a space-separated list of
    /// their names and read back the same way. <c>none</c> is the empty set.
    /// <para>
    /// Deliberately <em>not</em> a <see cref="Choice"/>, and deliberately carrying no
    /// <see cref="Choices"/>: ↑↓ step one-of-N, and bold-and-underline is not one of anything. Leaving
    /// <see cref="Choices"/> null is also what keeps the chrome honest — the <c>↑↓ choose</c> hint is
    /// derived from it, so a field with nothing to cycle cannot advertise the keys.
    /// </para>
    /// </summary>
    internal static ScreenField Flags<TEnum>(string label, Func<TEnum> get, Action<TEnum> set)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);

        return new ScreenField(
            label,
            () => FormatFlags(get()),
            value => UnknownFlag<TEnum>(value) is { } bad ? $"{label} has no '{bad}'" : null,
            value => set((TEnum)Enum.ToObject(typeof(TEnum), CombineFlags<TEnum>(value))));
    }

    /// <summary>What a flag set with nothing in it reads and is typed as.</summary>
    internal const string NoFlags = "none";

    /// <summary>The separators a flag list may be typed with — spaces, commas, or pipes.</summary>
    private static readonly char[] FlagSeparators = { ' ', '\t', ',', '|', '+' };

    /// <summary>
    /// Every non-zero member of a <c>[Flags]</c> enumeration, lower-cased, in declaration order — the
    /// vocabulary a <see cref="Flags{TEnum}"/> field accepts, and the list a screen draws so the words
    /// are discoverable without being typed blind.
    /// </summary>
    internal static IReadOnlyList<string> FlagNames<TEnum>() where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>()
            .Where(v => Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0)
            .Select(v => v.ToString()!.ToLowerInvariant())
            .ToArray();

    /// <summary>A flag set as <see cref="Flags{TEnum}"/> reads it: the set names, or <see cref="NoFlags"/>.</summary>
    internal static string FormatFlags<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var bits = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        var set = Enum.GetValues<TEnum>()
            .Select(v => (Name: v.ToString()!.ToLowerInvariant(), Bits: Convert.ToInt64(v, CultureInfo.InvariantCulture)))
            .Where(f => f.Bits != 0 && (bits & f.Bits) == f.Bits)
            .Select(f => f.Name)
            .ToArray();

        return set.Length == 0 ? NoFlags : string.Join(' ', set);
    }

    /// <summary>
    /// Whether a typed flag list names a given flag. Screens ask this rather than parsing, so a legend
    /// can follow the <em>buffer</em> while a field is open — the same rule F2's route radios follow,
    /// and the reason ↑↓ and typing both visibly do something before ⏎ commits anything.
    /// </summary>
    internal static bool FlagIsListed(string spec, string name) =>
        spec.Split(FlagSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Any(word => string.Equals(word, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first word of a flag list that names nothing, or null when every word is legal.</summary>
    private static string? UnknownFlag<TEnum>(string value) where TEnum : struct, Enum
    {
        var names = FlagNames<TEnum>();
        foreach (var word in value.Split(FlagSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(word, NoFlags, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!names.Contains(word, StringComparer.OrdinalIgnoreCase))
            {
                return word;
            }
        }

        return null;
    }

    /// <summary>The combined bits of a flag list that <see cref="UnknownFlag{TEnum}"/> has accepted.</summary>
    private static long CombineFlags<TEnum>(string value) where TEnum : struct, Enum
    {
        var members = Enum.GetValues<TEnum>()
            .Select(v => (Name: v.ToString()!, Bits: Convert.ToInt64(v, CultureInfo.InvariantCulture)))
            .ToArray();

        var bits = 0L;
        foreach (var word in value.Split(FlagSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var member in members)
            {
                if (string.Equals(member.Name, word, StringComparison.OrdinalIgnoreCase))
                {
                    bits |= member.Bits;
                }
            }
        }

        return bits;
    }

    /// <summary>
    /// The name of a window to route output to. Free text, with the windows already in use offered
    /// as ↑↓ suggestions — deliberately not a <see cref="Choice"/>, because the set of spawn windows
    /// is defined by what triggers route to, so a closed list could only ever re-use a window that
    /// already exists and there would be no way to create one.
    /// <para>
    /// A window name is a tab title, so it is rejected when blank or when it carries control
    /// characters that would corrupt the tab strip; it is otherwise whatever the user calls it.
    /// </para>
    /// </summary>
    internal static ScreenField WindowName(
        string label, Func<string> get, Action<string> set, IReadOnlyList<string> known)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(known);

        return new ScreenField(
            label,
            get,
            value => string.IsNullOrWhiteSpace(value)
                ? $"{label} cannot be empty"
                : value.Any(char.IsControl) ? $"{label} cannot contain control characters" : null,
            value => set(value.Trim()),
            known);
    }

    /// <summary>
    /// A .NET regular expression, rejected unless it actually compiles — a trigger or alias whose
    /// pattern doesn't parse would throw on the next line the engine matched, not here.
    /// </summary>
    internal static ScreenField Pattern(string label, Func<string> get, Action<string> set)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);

        return new ScreenField(label, get, ValidatePattern, set);
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
            value => set(ExpandBreaks(value)));
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
            });
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
            });
    }

    /// <summary>
    /// One of a fixed set of names, matched case-insensitively and stored canonically. Its list is
    /// <em>closed</em>: the validator refuses everything outside it, so the chrome draws it as the
    /// permitted set rather than as suggestions.
    /// </summary>
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
            choices,
            ClosedChoices: true);
    }

    /// <summary>
    /// A colour, or no colour at all. <see cref="ScreenColours.Palette"/> is what ↑↓ steps through,
    /// but the validator is deliberately wider than the palette: <c>#rrggbb</c> and <c>idx:N</c> are
    /// accepted too, because a <see cref="SharpMUTerm.Core.Text.TerminalColor"/> already in config may
    /// be a colour no short palette names, and opening the field on a value it would then refuse to
    /// commit would make an existing highlight uneditable.
    /// </summary>
    internal static ScreenField Colour(
        string label, Func<TerminalColor?> get, Action<TerminalColor?> set)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);

        return new ScreenField(
            label,
            () => ScreenColours.Format(get()),
            value => ScreenColours.TryParse(value, out _)
                ? null
                : $"{label} must be a colour name, #rrggbb, idx:N or none",
            value =>
            {
                if (ScreenColours.TryParse(value, out var parsed))
                {
                    set(parsed);
                }
            },
            ScreenColours.Palette);
    }

    /// <summary>
    /// An enum value, typed or picked by name — a character's log format is the canonical case. Its
    /// list is <em>closed</em>: the enum's members are the only values there are, and the chrome says so
    /// rather than implying a fifth log format could be typed into existence.
    /// </summary>
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
            names,
            ClosedChoices: true);
    }

    /// <summary>
    /// A key binding, taken from the keyboard rather than typed. Opening it arms a capture: the next
    /// keystroke <em>is</em> the value, and Esc — the only key that is never a candidate — abandons it.
    /// <para>
    /// It is not a <see cref="Choice"/> over key names and not a <see cref="Text"/> field, because both
    /// would ask the user to spell a chord they can simply press, and to know this client's spelling of
    /// it. What comes back is always canonical (<see cref="MacroKey.Canonicalise"/>), so
    /// <c>Ctrl+Shift+F1</c> is stored one way however the terminal reports it.
    /// </para>
    /// <para>
    /// Two things are refused, and both are refused <em>here</em> rather than left to be discovered by
    /// pressing the key and watching nothing happen. A chord this host cannot deliver — the whole numpad,
    /// Ctrl+Alt, the app's own shortcuts — is refused with <see cref="MacroKeys.Verdict"/>'s reason. And
    /// a chord another binding already holds is refused by <paramref name="taken"/>, because the engine
    /// resolves one macro per key and the second of two would silently never run, which is exactly the
    /// dead row this field exists to make impossible.
    /// </para>
    /// </summary>
    /// <param name="taken">Names the binding already holding a descriptor, or null when it is free.</param>
    internal static ScreenField Key(
        string label, Func<string> get, Action<string> set, Func<string, string?> taken)
    {
        ArgumentNullException.ThrowIfNull(get);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(taken);

        return new ScreenField(
            label,
            get,
            value => MacroKeys.Verdict(value) is { Fires: false } verdict ? verdict.Reason : taken(value),
            value => set(MacroKey.Canonicalise(value) ?? value.Trim()),
            Capture: true);
    }

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
