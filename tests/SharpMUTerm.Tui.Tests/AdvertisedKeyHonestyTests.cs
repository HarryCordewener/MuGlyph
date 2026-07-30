using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Commands;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Graphics;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// The honesty rule, applied to the keys this change advertises. The settings screens are already held to
/// it in one direction — a screen may not name a key that does something else — and the newline chord was
/// a case study in the other: <c>⌃L</c> worked, was named in a code comment and nowhere else, and was
/// therefore reported as a missing feature. So both halves are pinned here.
/// <para>
/// <b>Forwards:</b> every chord newly named on a surface really does what the surface says. <b>Backwards:</b>
/// no surface names a chord that cannot fire on this host — which is the specific trap for item 3, because
/// the two chords that were <em>asked for</em>, Shift+⏎ and Ctrl+⏎, are exactly those. SharpConsoleUI's
/// input parser decodes no CSI-u and enables no <c>modifyOtherKeys</c>, so a Unix terminal delivers both as
/// a bare <see cref="ConsoleKey.Enter"/> with no modifier bits; a hint promising them would send the reader
/// to press a key that sends their line instead of breaking it.
/// </para>
/// </summary>
/// <remarks>
/// Serialised for the same reason <see cref="PaneDragEndToEndTests"/> is: rendering a frame redirects the
/// process-global <c>Console.Out</c>, and the harness redirects <c>Console.In</c>.
/// </remarks>
[NotInParallel]
public class AdvertisedKeyHonestyTests
{
    private static readonly TerminalCapabilities Headless =
        new(GraphicsProtocol.None, supportsTrueColor: true, supportsKittyGraphics: false, supportsSixel: false);

    private static SharpMUTermApp App()
    {
        Console.SetIn(TextReader.Null);
        return new SharpMUTermApp(DemoScene.Build(), Headless, new HeadlessConsoleDriver(160, 48));
    }

    private static ConsoleKeyInfo Ctrl(ConsoleKey key) => new('\0', key, false, false, true);

    /// <summary>The four directional entries, and the arrow each one's subtitle has to name.</summary>
    private static readonly (string Id, ConsoleKey Key, string Arrow)[] Directions =
    {
        ("layout:focus-left", ConsoleKey.LeftArrow, "⌃←"),
        ("layout:focus-right", ConsoleKey.RightArrow, "⌃→"),
        ("layout:focus-up", ConsoleKey.UpArrow, "⌃↑"),
        ("layout:focus-down", ConsoleKey.DownArrow, "⌃↓"),
    };

    // --- forwards: the surface names it, and it works -------------------------------------------

    /// <summary>
    /// Each <c>Focus pane …</c> entry names its arrow, and pressing that arrow reaches the same pane the
    /// entry does. Both halves together are the claim: a subtitle naming a chord that moved somewhere else
    /// would be worse than no subtitle.
    /// </summary>
    [Test]
    public async Task EveryDirectionalEntryNamesTheArrowThatDoesTheSameThing()
    {
        foreach (var (id, key, arrow) in Directions)
        {
            var viaKey = App();
            viaKey.RenderSnapshot("split");
            viaKey.SimulateKey(Ctrl(key));

            var viaEntry = App();
            viaEntry.RenderSnapshot("split");
            viaEntry.DispatchCommand(id);

            var entry = viaEntry.BuildCatalog().Single(c => c.Id == id);
            await Assert.That(entry.Subtitle)
                .IsEqualTo(arrow)
                .Because($"'{entry.Title}' has to name the chord that runs it");
            await Assert.That(viaKey.FocusedPaneId)
                .IsEqualTo(viaEntry.FocusedPaneId)
                .Because($"{arrow} and '{entry.Title}' must reach the same pane");
        }
    }

    /// <summary>
    /// The newline entry names both spellings, and both of them insert a newline. Alt+⏎ is driven the way
    /// the terminal delivers it — Escape then Enter — because that is the only way it arrives.
    /// </summary>
    [Test]
    public async Task TheNewlineEntryNamesTwoChordsAndBothWork()
    {
        var app = App();
        app.RenderSnapshot();

        var entry = app.BuildCatalog().Single(c => c.Id == "term:newline");
        await Assert.That(entry.Subtitle).Contains("Alt+⏎");
        await Assert.That(entry.Subtitle).Contains("⌃L");

        // Alt+⏎, as two key events.
        Clear(app);
        app.SimulateKey(new ConsoleKeyInfo('a', ConsoleKey.NoName, false, false, false));
        app.SimulateKey(new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false));
        app.SimulateKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));
        app.SimulateKey(new ConsoleKeyInfo('b', ConsoleKey.NoName, false, false, false));
        await Assert.That(app.ArmedInputText).IsEqualTo("a\nb");

        // ⌃L.
        Clear(app);
        app.SimulateKey(new ConsoleKeyInfo('c', ConsoleKey.NoName, false, false, false));
        app.SimulateKey(Ctrl(ConsoleKey.L));
        app.SimulateKey(new ConsoleKeyInfo('d', ConsoleKey.NoName, false, false, false));
        await Assert.That(app.ArmedInputText).IsEqualTo("c\nd");
    }

    private static void Clear(SharpMUTermApp app)
    {
        app.SimulateKey(Ctrl(ConsoleKey.E));
        app.SimulateKey(Ctrl(ConsoleKey.U));
    }

    /// <summary>
    /// Every ⌃B chord a ⌃P subtitle now names is one the <em>armed prefix strip</em> also lists. Two
    /// surfaces advertising the same keymap have to agree, and this is the pair that can drift: the strip is
    /// a string in the header and the subtitles are strings in the catalog.
    /// <para>
    /// Checked against the strip rather than against <c>PaneCommands.Resolve</c>, which turns out not to be
    /// the whole keymap: <c>b</c> (collapse the rail), <c>m</c> (move mode) and <c>i</c> (the second command
    /// line) are handled by the app's own <c>RunPrefixCommand</c> and are not in the pure resolver at all.
    /// That split is pre-existing and outside this change, but it is the reason the authority here is the
    /// surface the user actually reads.
    /// </para>
    /// </summary>
    [Test]
    public async Task EveryPrefixChordNamedInASubtitleIsAlsoOnTheArmedStrip()
    {
        var app = App();
        app.RenderSnapshot("prefix"); // renders the header with the ⌃B strip armed
        var strip = app.HeaderText;
        await Assert.That(strip).Contains("⌃B");

        var named = app.BuildCatalog()
            .Where(c => c.Subtitle is not null && c.Subtitle.Contains("⌃B", StringComparison.Ordinal))
            .ToList();

        await Assert.That(named).IsNotEmpty();
        foreach (var item in named)
        {
            // "⌃B |" / "⌃O · ⌃B o" — the character after "⌃B ".
            var subtitle = item.Subtitle!;
            var key = subtitle[subtitle.IndexOf("⌃B ", StringComparison.Ordinal) + 3];
            await Assert.That(strip)
                .Contains(key.ToString())
                .Because($"'{item.Title}' advertises ⌃B {key}, so the armed strip must list it too");
        }
    }

    /// <summary>
    /// The status row's focus hint only names chords that can move something: it is there when the
    /// workspace has more than one pane or a second command line, and gone when neither is true. A hint for
    /// panes on a one-pane workspace is the same defect as a screen naming a key it does not offer, read
    /// from the other end.
    /// </summary>
    [Test]
    public async Task TheStatusHintIsOnlyThereWhenTheKeysCanMoveSomething()
    {
        var one = App();
        one.RenderSnapshot();
        await Assert.That(one.PaneIds.Count).IsEqualTo(1);
        await Assert.That(one.SecondBarShown).IsFalse();
        await Assert.That(one.StatusMarkup).DoesNotContain("pane");

        var split = App();
        split.RenderSnapshot("split");
        await Assert.That(split.PaneIds.Count).IsEqualTo(2);
        await Assert.That(split.StatusMarkup).Contains("⌃←→↑↓ pane");

        var bars = App();
        bars.RenderSnapshot("focus"); // split *and* a second command line
        await Assert.That(bars.StatusMarkup).Contains("⌃←→↑↓ pane");
        await Assert.That(bars.StatusMarkup).Contains("⇥");
    }

    /// <summary>
    /// ⇥, which the hint names, really does switch command lines — the pre-existing behaviour the hint
    /// newly advertises, checked rather than assumed.
    /// </summary>
    [Test]
    public async Task TheHintedTabKeySwitchesCommandLines()
    {
        var app = App();
        app.RenderSnapshot("focus");

        await Assert.That(app.SecondBarArmed).IsFalse();
        app.SimulateKey(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false));
        await Assert.That(app.SecondBarArmed).IsTrue();
    }

    // --- backwards: nothing names a chord that cannot fire ---------------------------------------

    /// <summary>
    /// <b>No surface promises Shift+⏎ or Ctrl+⏎.</b> They are what was asked for and this terminal cannot
    /// deliver them: the parser turns CR and LF alike into a bare Enter with no modifier bits, so a user
    /// following such a hint would send their line instead of breaking it. The command line still
    /// <em>accepts</em> them (the Windows <c>Console.ReadKey</c> path does report them and it costs nothing)
    /// — accepting is not advertising, and this is the line between the two.
    /// </summary>
    [Test]
    public async Task NoSurfacePromisesTheChordsThisTerminalCannotDeliver()
    {
        var app = App();
        app.RenderSnapshot("focus");

        var surfaces = new List<(string Where, string Text)>
        {
            ("--help", Program.UsageText),
            ("the status row", app.StatusMarkup),
            ("the ⌃P surface", string.Join("\n", app.BuildCatalog().Select(c => $"{c.Title} {c.Subtitle}"))),
        };

        foreach (var (where, text) in surfaces)
        {
            foreach (var forbidden in new[] { "Shift+Enter", "Shift+⏎", "Ctrl+Enter", "Ctrl+⏎", "⌃⏎" })
            {
                await Assert.That(text)
                    .DoesNotContain(forbidden)
                    .Because($"{where} must not advertise {forbidden}: this host reports it as a bare Enter");
            }
        }
    }

    /// <summary>
    /// The reason for the test above, stated as behaviour: an Enter carrying no modifiers <em>sends</em>.
    /// So a surface naming Shift+⏎ would be pointing at the send key.
    /// </summary>
    [Test]
    public async Task AnEnterWithoutModifierBitsSends()
    {
        var app = App();
        app.RenderSnapshot();
        Clear(app);
        foreach (var c in "west")
        {
            app.SimulateKey(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false));
        }

        // This is what a Unix terminal hands the app for ⏎, Shift+⏎ and Ctrl+⏎ alike.
        app.SimulateKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false));

        await Assert.That(app.ArmedInputText).IsEqualTo(string.Empty);
    }

    /// <summary>
    /// <c>--help</c> names the chords this change added, and names them in the spelling a reader will find
    /// on their keyboard. The page is the only documentation a first-time user reads before the client
    /// opens, and item 3 was reported precisely because a working chord appeared in no such place.
    /// </summary>
    [Test]
    public async Task HelpNamesTheNewChords()
    {
        var help = Program.UsageText;

        await Assert.That(help).Contains("Alt+Enter");
        await Assert.That(help).Contains("Ctrl+L");
        await Assert.That(help).Contains("Ctrl+Left/Right/Up/Down");
        await Assert.That(help).Contains("Alt+Left/Right");
    }

    /// <summary>
    /// <c>--help</c> no longer claims Ctrl+O is "next pane" while listing nothing about directional
    /// movement — and the chords it does name are ones the app claims. ⌃O is checked against the app's own
    /// shortcut list rather than against a memory of what it was bound to.
    /// </summary>
    [Test]
    public async Task HelpsChordsAreOnesTheAppReallyClaims()
    {
        var claimed = MacroKeys.AppShortcuts;

        await Assert.That(claimed.Any(s => s.Key == ConsoleKey.O && s.Modifiers == ConsoleModifiers.Control))
            .IsTrue();
        await Assert.That(Program.UsageText).Contains("Ctrl+O cycles them");
    }

    /// <summary>
    /// <b>The Ctrl+arrows stay honest on the F4 screen.</b> They are claimed from the window's key handler
    /// rather than as global shortcuts, and deliberately <em>after</em> the macro dispatcher — so
    /// <see cref="MacroKeys.Verdict"/> still reporting a macro on <c>Ctrl+Left</c> as one that fires is
    /// true, and the keypad screen is not lying. This pins that ordering: a later change moving pane
    /// navigation ahead of the macros, or into <see cref="MacroKeys.AppShortcuts"/>, has to update F4 too.
    /// </summary>
    [Test]
    public async Task AMacroOnACtrlArrowStillOutranksPaneNavigation()
    {
        foreach (var descriptor in new[] { "Ctrl+Left", "Ctrl+Right", "Ctrl+Up", "Ctrl+Down" })
        {
            await Assert.That(MacroKeys.Verdict(descriptor).Fires)
                .IsTrue()
                .Because($"F4 says a macro on {descriptor} fires, so pane navigation must not pre-empt it");
        }

        // And they are not global shortcuts, which is what would pre-empt it.
        foreach (var key in new[]
        {
            ConsoleKey.LeftArrow, ConsoleKey.RightArrow, ConsoleKey.UpArrow, ConsoleKey.DownArrow,
        })
        {
            await Assert.That(MacroKeys.AppShortcuts.Any(s => s.Key == key)).IsFalse();
        }
    }

    /// <summary>
    /// The claim above, as behaviour: a macro bound to ⌃→ runs and the pane focus does <em>not</em> move.
    /// A key the user has deliberately bound is theirs, which is the rule the rest of this chain follows.
    /// <para>
    /// The world has to be bound for this to be visible at all — <c>DispatchMacro</c> resolves the binding
    /// and then sends it through the session, and with nothing bound there is nothing to send to, so the key
    /// falls through. That fall-through is deliberate and tested elsewhere; here it would hide the ordering.
    /// </para>
    /// </summary>
    [Test]
    public async Task ABoundMacroWinsTheChordAndFocusStaysPut()
    {
        Console.SetIn(TextReader.Null);
        var config = DemoScene.Build();
        config.TriggerSets[0].Macros.Add(new SharpMUTerm.Core.Automation.Macro
        {
            Name = "east", Key = "Ctrl+Right", Command = "east", Enabled = true,
        });

        var app = new SharpMUTermApp(config, Headless, new HeadlessConsoleDriver(160, 48));
        app.BindWorldWithoutConnecting(config.Worlds[0]);
        app.RenderSnapshot("split");

        var before = app.FocusedPaneId;
        var sent = app.SimulateKey(Ctrl(ConsoleKey.RightArrow));

        await Assert.That(sent).IsEqualTo("east");
        await Assert.That(app.FocusedPaneId).IsEqualTo(before);
    }
}
