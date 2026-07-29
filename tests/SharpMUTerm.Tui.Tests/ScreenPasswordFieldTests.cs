using System.Text.RegularExpressions;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The character's password: a real field now, masked everywhere it is drawn, and honest about where the
/// value goes — which is nowhere. It used to be a readout labelled <c>keychain</c>, an affordance-free row
/// advertising a credential store this codebase does not contain, and the reason it was a readout was that
/// there was genuinely nowhere to put a password: the only way to persist a working login line was to bake
/// the secret into the connect string in plaintext, where it <em>was</em> serialized.
/// <para>
/// Two properties are pinned here, and both matter more than the row's appearance. The plaintext must
/// never reach rendered markup — a frame is a thing snapshots write to disk and screenshots publish — and
/// the label must not claim storage that does not exist.
/// </para>
/// </summary>
public class ScreenPasswordFieldTests
{
    /// <summary>The value under test: distinctive, so finding it in a frame is unambiguous.</summary>
    private const string Secret = "zvxq-canary-71";

    /// <summary>The field well as it appears in markup (see <see cref="ScreenReadOnlyTests"/>).</summary>
    private const string Well = "on " + ScreenPalette.FieldBg;

    /// <summary>The accent block an open field paints its caret in.</summary>
    private const string Caret = "[" + ScreenPalette.Ink + " on " + ScreenPalette.Accent + "]";

    private static List<WorldDefinition> Worlds(string? password = Secret, string? connect = null) => new()
    {
        new WorldDefinition
        {
            Name = "Aetherfall",
            Host = "aetherfall.mux",
            Port = 4201,
            Characters = new List<CharacterDefinition>
            {
                new()
                {
                    Name = "Corvid",
                    Password = password,
                    ConnectString = connect,
                    AutoLogin = true,
                    OnConnect = "look",
                },
            },
        },
    };

    private static List<string> Form(
        IReadOnlyList<WorldDefinition> worlds, ScreenFocus? focus = null, int selected = 0) =>
        WorldsScreenRenderer.FormColumn(worlds[0].Characters[0], ScreenPalette.Accent, focus, selected);

    private static ScreenFocus Editing(int field, string text, int? caret = null) => new(
        WorldsScreenRenderer.CharactersPane,
        0,
        new ScreenFieldEdit(field, text, caret ?? text.Length, null, RowFields: 6, Masked: IsMasked(field)));

    private static bool IsMasked(int field) => field == WorldsScreenRenderer.PasswordField;

    private static string Row(IReadOnlyList<string> lines, string label) =>
        lines.Single(l => Regex.IsMatch(Visible(l), $@"^\s*{Regex.Escape(label)}\s\s"));

    /// <summary>A markup line as it prints: tags stripped, escaped brackets folded back to one.</summary>
    private static string Visible(string markup)
    {
        var guarded = markup.Replace("[[", "\u0001", StringComparison.Ordinal)
            .Replace("]]", "\u0002", StringComparison.Ordinal);
        return Regex.Replace(guarded, @"\[[^\[\]]*\]", string.Empty)
            .Replace('\u0001', '[')
            .Replace('\u0002', ']');
    }

    private static int MaskGlyphs(string line) => line.Count(c => c == ScreenChrome.MaskGlyph);

    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

    private static ConsoleKeyInfo Char(char c) => new(c, ConsoleKey.NoName, false, false, false);

    /// <summary>
    /// Opens the F5 screen on the selected character's own row with its first field being typed into —
    /// the state the keyboard reaches with ⇥ ⇥ ⏎ — and hands back the session so a test can step on.
    /// </summary>
    private static SettingsSession OnTheCharactersName(IReadOnlyList<WorldDefinition> worlds)
    {
        var session = new SettingsSession(selection => WorldsScreenRenderer.Model(
            worlds,
            Array.Empty<TriggerSet>(),
            selection.SelectionIn(WorldsScreenRenderer.WorldsPane),
            selection.SelectionIn(WorldsScreenRenderer.CharactersPane)));

        session.Handle(Key(ConsoleKey.Tab));   // the world's security checkboxes
        session.Handle(Key(ConsoleKey.Tab));   // the character's own row
        session.Handle(Key(ConsoleKey.Enter)); // opens the name
        return session;
    }

    // ---- the mask ------------------------------------------------------------------------------------

    /// <summary>
    /// At rest: a fixed-width mask in a well, with the note on the row beneath it. The mask is
    /// <see cref="ScreenChrome.RestingMaskWidth"/> glyphs whatever the password is, because a resting
    /// screen is the one a screenshot catches and a mask that grew with the secret would publish its
    /// length.
    /// </summary>
    [Test]
    public async Task TheRestingPasswordRowIsMaskedInAWellAndSaysItIsNotSaved()
    {
        var form = Form(Worlds());
        var row = Row(form, "password");

        await Assert.That(row).DoesNotContain(Secret);
        await Assert.That(MaskGlyphs(row)).IsEqualTo(ScreenChrome.RestingMaskWidth);
        await Assert.That(row).Contains(Well);

        // The note is the very next row, so it reads as belonging to the value above it.
        var note = form[form.IndexOf(row) + 1];
        await Assert.That(Visible(note).Trim()).IsEqualTo(WorldsScreenRenderer.SessionOnlyNote);
    }

    /// <summary>
    /// The note has a row of its own because it cannot share one. Beside the value it had to fit a
    /// 48-cell panel next to a field whose drawn width is the buffer's, so it wrapped as soon as a
    /// password existed — and a wrapped row costs the form a line the view did not measure, which pushed
    /// <c>log folder</c> out of the character band entirely. This pins the width instead of the symptom:
    /// every row of the form fits the panel it is drawn in, with a password set and with one being typed.
    /// </summary>
    [Test]
    public async Task EveryFormRowFitsThePanelWithAPasswordSetAndWithOneBeingTyped()
    {
        // The form panel is 48 cells wide and ScreenChrome.Indent spends one of them (WorldsScreenView).
        const int panel = 48 - 1;

        var forms = new[]
        {
            Form(Worlds()),
            Form(Worlds(password: null)),
            Form(Worlds(), Editing(WorldsScreenRenderer.PasswordField, Secret)),
            Form(Worlds(password: null), Editing(WorldsScreenRenderer.PasswordField, string.Empty)),
        };

        foreach (var form in forms)
        {
            foreach (var line in form)
            {
                await Assert.That(MarkupText.VisibleLength(line)).IsLessThanOrEqualTo(panel).Because(line);
            }
        }
    }

    /// <summary>
    /// Two different passwords of two different lengths draw the same row. This is the length-hiding
    /// property stated as an equality, which is the only way to pin it that a longer default mask
    /// wouldn't silently satisfy.
    /// </summary>
    [Test]
    public async Task TheRestingMaskRevealsNothingAboutTheValuesLength()
    {
        var shortest = Row(Form(Worlds("a")), "password");
        var longest = Row(Form(Worlds(new string('x', 64))), "password");

        await Assert.That(shortest).IsEqualTo(longest);
    }

    /// <summary>
    /// With no password the well says so in words rather than drawing dots over nothing — a mask standing
    /// for an unset value would claim a password that isn't there — and the note is unchanged, because
    /// where the value would go is the same answer either way.
    /// </summary>
    [Test]
    public async Task WithNoPasswordSetTheWellSaysSoAndTheNoteIsUnchanged()
    {
        var form = Form(Worlds(password: null));
        var row = Row(form, "password");

        await Assert.That(MaskGlyphs(row)).IsEqualTo(0);
        await Assert.That(row).Contains(Well);
        await Assert.That(Visible(row)).Contains(WorldsScreenRenderer.NoPassword);
        await Assert.That(Visible(form[form.IndexOf(row) + 1]).Trim())
            .IsEqualTo(WorldsScreenRenderer.SessionOnlyNote);
    }

    /// <summary>
    /// Mid-edit: the buffer holds the real value — it is what ⏎ commits and what Backspace edits — and the
    /// row draws one dot per character with the caret inside them. This is the property the whole masking
    /// design rests on, and it is asserted over the <em>markup</em>, because markup is what a snapshot
    /// writes to disk.
    /// </summary>
    [Test]
    public async Task AnOpenPasswordBufferIsDrawnAsDotsWithTheCaretInsideThem()
    {
        var form = Form(Worlds(), Editing(WorldsScreenRenderer.PasswordField, Secret));
        var row = form.Single(l => l.Contains(Caret, StringComparison.Ordinal));

        await Assert.That(string.Join("\n", form)).DoesNotContain(Secret);
        await Assert.That(Visible(row)).Contains("password");
        await Assert.That(MaskGlyphs(row)).IsEqualTo(Secret.Length);
        await Assert.That(form.Count(l => l.Contains(Caret, StringComparison.Ordinal))).IsEqualTo(1);
    }

    /// <summary>
    /// The caret moves inside the mask, so ← → visibly do something without the value being shown. With
    /// the caret in the middle the dots split either side of it; the total is unchanged.
    /// </summary>
    [Test]
    public async Task TheCaretMovesWithinTheMaskWithoutRevealingWhatItIsOn()
    {
        var form = Form(Worlds(), Editing(WorldsScreenRenderer.PasswordField, Secret, caret: 3));
        var row = form.Single(l => l.Contains(Caret, StringComparison.Ordinal));

        await Assert.That(row).DoesNotContain(Secret);
        await Assert.That(MaskGlyphs(row)).IsEqualTo(Secret.Length);

        // Three dots, then the caret block, then the rest: the buffer's shape without its content.
        var caretAt = row.IndexOf(Caret, StringComparison.Ordinal);
        await Assert.That(MaskGlyphs(row[..caretAt])).IsEqualTo(3);
    }

    /// <summary>
    /// A password made of markup cannot become markup. Every other field escapes its buffer; this one
    /// never renders it at all, which is the stronger guarantee — so the awkward value is not merely
    /// escaped, it is absent.
    /// </summary>
    [Test]
    public async Task APasswordOfMarkupIsNeitherRenderedNorInterpreted()
    {
        const string awkward = "[bold red]zvxq[/]";
        var form = Form(Worlds(awkward), Editing(WorldsScreenRenderer.PasswordField, awkward));
        var joined = string.Join("\n", form);

        // Not interpreted (no live tag), not escaped-and-shown (no [[bold), not there at all.
        await Assert.That(joined).DoesNotContain("bold red");
        await Assert.That(joined).DoesNotContain("zvxq");
        await Assert.That(MaskGlyphs(form.Single(l => l.Contains(Caret, StringComparison.Ordinal))))
            .IsEqualTo(awkward.Length);
    }

    /// <summary>
    /// No open field of this row lists candidates that could carry the buffer — the password field has no
    /// choices at all, so the dropdown that draws an open field's entries has nothing to draw and cannot
    /// become a second route from the buffer to the screen.
    /// </summary>
    [Test]
    public async Task ThePasswordFieldOffersNoDropdownForTheBufferToEscapeThrough()
    {
        var field = WorldsScreenRenderer.Model(Worlds(), Array.Empty<TriggerSet>(), 0, 0)
            .FieldAt(WorldsScreenRenderer.CharactersPane, 0, WorldsScreenRenderer.PasswordField)!.Value;

        await Assert.That(field.Masked).IsTrue();
        await Assert.That(field.Choices).IsNull();
    }

    // ---- honesty ------------------------------------------------------------------------------------

    /// <summary>
    /// The row no longer claims a keychain. There is no keychain, no DPAPI and no libsecret behind this
    /// field — the word appeared exactly once in the whole codebase, on this label — and a row that
    /// promises a credential store will be believed by the person typing into it.
    /// </summary>
    [Test]
    public async Task NothingOnTheCharacterFormClaimsACredentialStore()
    {
        foreach (var password in new[] { Secret, null })
        {
            var form = string.Join("\n", Form(Worlds(password)));

            foreach (var claim in new[] { "keychain", "keyring", "credential", "DPAPI", "libsecret", "vault" })
            {
                await Assert.That(form.ToLowerInvariant()).DoesNotContain(claim.ToLowerInvariant()).Because(claim);
            }
        }
    }

    /// <summary>
    /// And it says the true thing instead, in words rather than by omission: the value lives for this
    /// session and is not written anywhere. Pinned against the definition it describes — if the password
    /// ever stops being <c>[JsonIgnore]</c>, this note becomes a lie and the test has to be revisited.
    /// </summary>
    [Test]
    public async Task ThePasswordRowStatesWhatActuallyHappensToTheValue()
    {
        await Assert.That(WorldsScreenRenderer.SessionOnlyNote).Contains("this session only");
        await Assert.That(WorldsScreenRenderer.SessionOnlyNote).Contains("never saved");

        var json = ConfigurationStore.Serialize(new AppConfiguration { Worlds = Worlds() });
        await Assert.That(json).DoesNotContain(Secret);
    }

    // ---- typing into it -----------------------------------------------------------------------------

    /// <summary>
    /// End to end from the keyboard: ⇥ from the name reaches the password, typing fills it, ⏎ commits it
    /// to the in-memory <see cref="CharacterDefinition.Password"/> — and the frame drawn from that state
    /// still shows dots.
    /// </summary>
    [Test]
    public async Task TypingIntoTheFieldSetsTheSessionPasswordAndTheFrameStaysMasked()
    {
        var worlds = Worlds(password: null);
        var character = worlds[0].Characters[0];
        var session = OnTheCharactersName(worlds);

        session.Handle(Key(ConsoleKey.Tab));
        await Assert.That(session.Focus().Edit!.Value.Field).IsEqualTo(WorldsScreenRenderer.PasswordField);
        await Assert.That(session.Focus().Edit!.Value.Masked).IsTrue();

        foreach (var c in Secret)
        {
            session.Handle(Char(c));
        }

        // Mid-edit, before the commit: the buffer already holds the secret and the frame already doesn't.
        var midEdit = string.Join("\n", Form(worlds, session.Focus()));
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo(Secret);
        await Assert.That(midEdit).DoesNotContain(Secret);

        session.Handle(Key(ConsoleKey.Enter));

        await Assert.That(character.Password).IsEqualTo(Secret);
        await Assert.That(string.Join("\n", Form(worlds))).DoesNotContain(Secret);
    }

    /// <summary>
    /// Esc reverts it like any other field — the undo log holds the old value, not the new one — and
    /// blanking the field is how a password is forgotten before the process is.
    /// </summary>
    [Test]
    public async Task EscapeRevertsThePasswordAndBlankingItForgetsIt()
    {
        var worlds = Worlds();
        var character = worlds[0].Characters[0];
        var session = OnTheCharactersName(worlds);
        session.Handle(Key(ConsoleKey.Tab));

        // The edit opens on the real value, so an existing password can be corrected rather than retyped.
        await Assert.That(session.Focus().Edit!.Value.Text).IsEqualTo(Secret);

        foreach (var c in "-extra")
        {
            session.Handle(Char(c));
        }

        session.Handle(Key(ConsoleKey.Enter));
        await Assert.That(character.Password).IsEqualTo(Secret + "-extra");

        session.Edits.Revert();
        await Assert.That(character.Password).IsEqualTo(Secret);

        // Blanked: null, not "", so "no password" has one spelling — and the login line drops the token's
        // space with it rather than sending a trailing one.
        var field = WorldsScreenRenderer.Model(worlds, Array.Empty<TriggerSet>(), 0, 0)
            .FieldAt(WorldsScreenRenderer.CharactersPane, 0, WorldsScreenRenderer.PasswordField)!.Value;
        await Assert.That(new ScreenEdits().Apply(field, string.Empty)).IsNull();
        await Assert.That(character.Password).IsNull();
        await Assert.That(character.ResolveConnectString()).IsEqualTo("connect Corvid");
    }

    /// <summary>
    /// A committed password is <b>not trimmed</b>, which is the one place this field departs from every
    /// other text field on these screens. A trimmed name is a tidier name; a trimmed secret is a
    /// different secret, and it would fail at the server with the field on screen showing what was typed.
    /// </summary>
    [Test]
    public async Task ACommittedPasswordKeepsItsSurroundingSpaces()
    {
        var worlds = Worlds(password: null);
        var field = WorldsScreenRenderer.Model(worlds, Array.Empty<TriggerSet>(), 0, 0)
            .FieldAt(WorldsScreenRenderer.CharactersPane, 0, WorldsScreenRenderer.PasswordField)!.Value;

        await Assert.That(new ScreenEdits().Apply(field, " spaced ")).IsNull();
        await Assert.That(worlds[0].Characters[0].Password).IsEqualTo(" spaced ");
    }

    /// <summary>
    /// A control character is refused at the field, and the refusal names the field rather than quoting
    /// the value — a rejection message is drawn on the row, so one carrying the buffer would undo the
    /// masking at the worst possible moment.
    /// </summary>
    [Test]
    public async Task AControlCharacterIsRefusedWithoutQuotingTheValue()
    {
        var worlds = Worlds(password: null);
        var field = WorldsScreenRenderer.Model(worlds, Array.Empty<TriggerSet>(), 0, 0)
            .FieldAt(WorldsScreenRenderer.CharactersPane, 0, WorldsScreenRenderer.PasswordField)!.Value;

        var error = new ScreenEdits().Apply(field, "two\nlines");

        await Assert.That(error).IsNotNull();
        await Assert.That(error!).DoesNotContain("two");
        await Assert.That(error!).DoesNotContain("lines");
        await Assert.That(worlds[0].Characters[0].Password).IsNull();
    }

    /// <summary>
    /// <c>[[⧉ duplicate]]</c> carries the password over — that is deliberate and documented on
    /// <see cref="CharacterDefinition.Clone"/>, since a copy of a logged-in character that silently forgot
    /// its password would be the worse surprise — and it still reaches nothing on disk. The button is the
    /// one place a password is copied rather than typed, so this is the surface where "it is only ever in
    /// memory" is worth checking rather than assuming.
    /// </summary>
    [Test]
    public async Task DuplicatingACharacterCopiesThePasswordInMemoryAndNowhereElse()
    {
        var worlds = Worlds();
        var model = WorldsScreenRenderer.Model(worlds, Array.Empty<TriggerSet>(), 0, 0);

        var duplicate = Enumerable.Range(0, model.Sizes[WorldsScreenRenderer.CharactersPane])
            .Select(i => model.RowAt(WorldsScreenRenderer.CharactersPane, i).Button)
            .Single(b => b?.Label == WorldsScreenRenderer.DuplicateCharacterLabel)!.Value;

        new ScreenEdits().Apply(duplicate);

        var copy = worlds[0].Characters[1];
        await Assert.That(copy.Password).IsEqualTo(Secret);
        await Assert.That(copy.Name).IsNotEqualTo("Corvid");

        // Two characters holding it now, and still no route to the file.
        var json = ConfigurationStore.Serialize(new AppConfiguration { Worlds = worlds });
        await Assert.That(json).DoesNotContain(Secret);

        // Nor to the copy's own drawn form.
        await Assert.That(string.Join(
                "\n", WorldsScreenRenderer.FormColumn(copy, ScreenPalette.Accent, null, 1)))
            .DoesNotContain(Secret);
    }

    /// <summary>
    /// The demo configuration — what every snapshot and every <c>--demo-config</c> run renders — carries
    /// no password at all, and no connect string with anything password-shaped baked into it. Demo data is
    /// published in frames and pasted into issues, so a plausible-looking secret in it would eventually be
    /// read as a real one, and a demo that shipped a login line with a password in it would be teaching
    /// exactly the habit the template exists to end.
    /// </summary>
    [Test]
    public async Task TheDemoConfigurationCarriesNoPasswordAndNoBakedInLoginLine()
    {
        foreach (var character in DemoScene.Build().Worlds.SelectMany(w => w.Characters))
        {
            await Assert.That(character.Password).IsNull().Because(character.Name);

            // Null means "the default template", which is the state a demo should be showing off.
            await Assert.That(character.ConnectString).IsNull().Because(character.Name);
            await Assert.That(character.ResolveConnectString())
                .IsEqualTo($"connect {character.Name}")
                .Because(character.Name);
        }
    }

    // ---- the connect line the password is substituted into ------------------------------------------

    /// <summary>
    /// The connect line is drawn, welled and editable, and an untouched one shows the default template
    /// rather than an empty box — a login line nobody has edited still <em>has</em> a value, and this is
    /// the only place the token syntax is visible at all.
    /// </summary>
    [Test]
    public async Task TheConnectLineShowsTheDefaultTemplateAndCanBeTyped()
    {
        var row = Row(Form(Worlds(connect: null)), "connect");

        await Assert.That(row).Contains(Well);
        await Assert.That(Visible(row)).Contains(ConnectStringTemplate.Default);

        var overridden = Row(Form(Worlds(connect: "co %CHARACTER% %PASSWORD%")), "connect");
        await Assert.That(Visible(overridden)).Contains("co %CHARACTER% %PASSWORD%");
    }

    /// <summary>
    /// Editing it stores a line; committing the default back — or blanking the field — stores null, so
    /// "unset" has one spelling in config and a later change to the default still reaches everyone who
    /// never overrode it. The default is offered as the single ↑↓ suggestion, which is the key that puts
    /// it back without retyping.
    /// </summary>
    [Test]
    public async Task CommittingTheDefaultBackStoresNullSoTheDefaultStaysTheDefault()
    {
        var worlds = Worlds(connect: null);
        var character = worlds[0].Characters[0];

        var field = WorldsScreenRenderer.Model(worlds, Array.Empty<TriggerSet>(), 0, 0)
            .FieldAt(WorldsScreenRenderer.CharactersPane, 0, WorldsScreenRenderer.ConnectStringField)!.Value;

        await Assert.That(field.Get()).IsEqualTo(ConnectStringTemplate.Default);
        await Assert.That(field.Choices).IsEquivalentTo(new[] { ConnectStringTemplate.Default });
        await Assert.That(field.ClosedChoices).IsFalse();

        await Assert.That(new ScreenEdits().Apply(field, "co %CHARACTER% %PASSWORD%")).IsNull();
        await Assert.That(character.ConnectString).IsEqualTo("co %CHARACTER% %PASSWORD%");

        await Assert.That(new ScreenEdits().Apply(field, ConnectStringTemplate.Default)).IsNull();
        await Assert.That(character.ConnectString).IsNull();

        character.ConnectString = "co %CHARACTER%";
        await Assert.That(new ScreenEdits().Apply(field, "   ")).IsNull();
        await Assert.That(character.ConnectString).IsNull();
    }
}
