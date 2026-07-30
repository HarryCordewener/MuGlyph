using System.Text.RegularExpressions;
using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Graphics;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The character's password: a real field, masked everywhere it is drawn, and honest about where the value
/// goes — which is <c>secrets.json</c>, in plaintext, and deliberately <em>not</em> <c>config.json</c>. It
/// used to be a readout labelled <c>keychain</c>, an affordance-free row advertising a credential store this
/// codebase does not contain.
/// <para>
/// <b>The note has now said four different things, and this file's assertions moved with it.</b> It read
/// <c>this session only — never saved</c> while the field was <c>[JsonIgnore]</c> session state; then
/// <c>saved in config.json, plain text</c>, honest about a design that put the secret in the file people
/// paste; and now it names the separate file the secret actually lives in. The rule these tests enforce has
/// not moved an inch — <em>the row must describe what actually happens to the value</em> — it is the
/// behaviour underneath that keeps moving, and a note left describing a previous design is a lie of the same
/// kind as <c>keychain</c>.
/// </para>
/// <para>
/// Three properties are pinned here, and all of them matter more than the row's appearance. The plaintext
/// must never reach rendered <em>markup</em> — a frame is a thing snapshots write to disk and screenshots
/// publish, and a screenshot with a live password in it is the leak that prompted the storage split. The
/// label must not claim storage that does not exist, in either direction: no credential store, and no
/// pretending the value is encrypted. And the value must reach the secrets file and not the config document,
/// which is the whole design in one sentence.
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
    public async Task TheRestingPasswordRowIsMaskedInAWellAndSaysWhereTheValueGoes()
    {
        var form = Form(Worlds());
        var row = Row(form, "password");

        await Assert.That(row).DoesNotContain(Secret);
        await Assert.That(MaskGlyphs(row)).IsEqualTo(ScreenChrome.RestingMaskWidth);
        await Assert.That(row).Contains(Well);

        // The note is the very next row, so it reads as belonging to the value above it.
        var note = form[form.IndexOf(row) + 1];
        await Assert.That(Visible(note).Trim()).IsEqualTo(WorldsScreenRenderer.StorageNote);
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
            .IsEqualTo(WorldsScreenRenderer.StorageNote);
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
    /// And it says the true thing instead, in words rather than by omission: the value is saved, it is
    /// plaintext, and it is in <c>secrets.json</c>. <b>This assertion has been inverted twice</b> — it once
    /// required the note to say <c>never saved</c>, then required it to name <c>config.json</c> — and each
    /// time it was pinned against the mechanism it describes, which is the only way a note like this is worth
    /// anything. Here that means both halves at once: the secret is in the secrets file, and it is not in the
    /// config document.
    /// <para>
    /// The strong word is required explicitly. <c>plain</c> is asserted, not merely <c>saved</c>, because
    /// "saved" alone is the gloss a reader fills in reassuringly, and because <c>encrypted</c> or
    /// <c>secure</c> would be the same class of lie as <c>keychain</c>. The words the note may <em>not</em>
    /// use include the two things it used to say, so a revert cannot pass quietly.
    /// </para>
    /// </summary>
    [Test]
    public async Task ThePasswordRowStatesWhatActuallyHappensToTheValue()
    {
        await Assert.That(WorldsScreenRenderer.StorageNote).Contains("saved");
        await Assert.That(WorldsScreenRenderer.StorageNote).Contains("plain");
        await Assert.That(WorldsScreenRenderer.StorageNote).Contains(SecretsStore.FileName);

        // The claim, checked against the pair of files that make it true.
        var directory = Path.Combine(Path.GetTempPath(), $"smuterm-note-{Guid.NewGuid():N}");
        var configPath = Path.Combine(directory, "config.json");
        try
        {
            ConfigurationStore.Save(configPath, new AppConfiguration { Worlds = Worlds() });

            await Assert.That(File.ReadAllText(configPath)).DoesNotContain(Secret);
            await Assert.That(File.ReadAllText(SecretsStore.PathFor(configPath))).Contains(Secret);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            {
                // Nothing a test should fail over.
            }
        }

        // And the words it must not use — including the two the row used to say, so a revert cannot pass.
        foreach (var lie in new[]
        {
            "encrypted", "secure", "safely", "never saved", "session only", "config.json",
        })
        {
            await Assert.That(WorldsScreenRenderer.StorageNote.ToLowerInvariant())
                .DoesNotContain(lie)
                .Because(lie);
        }
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
    /// Esc inside the field abandons the buffer like any other field — a half-typed secret never reaches
    /// config — a committed one is kept, and blanking the field is how a password is forgotten before the
    /// process is.
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

        // Esc *before* ⏎ is what abandons a half-typed secret — the buffer never reaches config. Once ⏎
        // has taken it, it is the password, and leaving the screen keeps it: the alternative is a client
        // that silently reconnects with the old credential.
        await Assert.That(session.Focus().Edit).IsNotNull();
        session.Handle(Key(ConsoleKey.Escape));
        await Assert.That(session.Focus().Edit).IsNull();
        await Assert.That(character.Password).IsEqualTo(Secret);

        // Re-open it the way the keyboard does — ⏎ opens the row's first field (the name), ⇥ steps to the
        // password — and commit this time.
        session.Handle(Key(ConsoleKey.Enter));
        session.Handle(Key(ConsoleKey.Tab));
        await Assert.That(session.Focus().Edit!.Value.Field).IsEqualTo(WorldsScreenRenderer.PasswordField);

        foreach (var c in "-extra")
        {
            session.Handle(Char(c));
        }

        session.Handle(Key(ConsoleKey.Enter));
        await Assert.That(character.Password).IsEqualTo(Secret + "-extra");

        session.Edits.Revert();
        await Assert.That(character.Password).IsEqualTo(Secret + "-extra");

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
    /// <c>[[⧉ duplicate]]</c> carries the password over — deliberate, and re-justified on
    /// <see cref="CharacterDefinition.Clone"/> now that the old justification ("nothing here reaches disk") is
    /// void. A duplicate that dropped it would look complete on screen, because the mask cannot distinguish
    /// "copied" from "cleared", and would then fail to log in.
    /// <para>
    /// <b>The disk assertion is inverted from "the secret is absent from the serialized config" to "it is
    /// absent and there are two references"</b>, which is the stronger claim: the old one held because nothing
    /// was saved, and this one holds while both characters' passwords are saved and reloadable.
    /// <see cref="CharacterDefinition.PasswordRef"/> is <em>not</em> copied, so the two get separate rows —
    /// pinned end to end in <c>PasswordAtRestTests.ADuplicatedCharacterGetsItsOwnSecretsRow</c>. The mask
    /// assertion is unchanged and still absolute: neither form draws it.
    /// </para>
    /// </summary>
    [Test]
    public async Task DuplicatingACharacterCopiesThePasswordButNotItsReferenceOrItsFrame()
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

        // The value, not the reference: the copy is destined for a row of its own.
        await Assert.That(copy.PasswordRef).IsNull();

        // Two characters holding the secret, and the config document still holds none of it.
        var json = ConfigurationStore.Serialize(new AppConfiguration { Worlds = worlds });
        await Assert.That(Occurrences(json, Secret)).IsEqualTo(0);

        // And still no route to a frame, for either of them.
        await Assert.That(string.Join(
                "\n", WorldsScreenRenderer.FormColumn(copy, ScreenPalette.Accent, null, 1)))
            .DoesNotContain(Secret);
        await Assert.That(string.Join("\n", Form(worlds))).DoesNotContain(Secret);
    }

    /// <summary>How many times <paramref name="needle"/> appears in <paramref name="haystack"/>.</summary>
    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
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

            // Nor a reference, so a demo run creates no secrets file and a demo config pasted anywhere shows
            // the shape of a character with no stored credential.
            await Assert.That(character.PasswordRef).IsNull().Because(character.Name);

            // Null means "the default template", which is the state a demo should be showing off.
            await Assert.That(character.ConnectString).IsNull().Because(character.Name);
            await Assert.That(character.ResolveConnectString())
                .IsEqualTo($"connect {character.Name}")
                .Because(character.Name);
        }
    }

    /// <summary>
    /// The whole frame, not just the form column: an F5 screen rendered over a config that <em>carries</em>
    /// a stored password emits no plaintext into the ANSI. This is the assertion that got more valuable
    /// rather than less when the password started persisting — before, a loaded config could not have one,
    /// so the only secret a frame could have leaked was one typed in the same process. Now every frame this
    /// app renders is rendered over a config that may hold credentials.
    /// <para>
    /// Both states are checked, because they are drawn by different code: the row at rest (a fixed-width
    /// mask) and the row mid-edit (one dot per character, caret inside). The app is constructed with no
    /// save action, so this also exercises the gate that keeps a frame from writing anybody's
    /// <c>config.json</c>.
    /// </para>
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task NoRenderedFrameOfTheF5ScreenCarriesAStoredPassword()
    {
        // Constructing the app touches the process-global console streams; a null reader keeps a headless
        // driver from blocking on stdin.
        Console.SetIn(TextReader.Null);

        var capabilities = new TerminalCapabilities(
            GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

        foreach (var view in new[] { "worlds", "worlds-edit", "password" })
        {
            var config = new AppConfiguration { Worlds = Worlds() };
            var app = new SharpMUTermApp(config, capabilities, new HeadlessConsoleDriver(140, 40));

            var frame = app.RenderSnapshot(view);

            await Assert.That(frame).IsNotEmpty().Because(view);
            await Assert.That(frame).DoesNotContain(Secret).Because(view);

            // The stored value is still there afterwards — the frame did not clear it to pass.
            await Assert.That(config.Worlds[0].Characters[0].Password).IsEqualTo(Secret).Because(view);
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
