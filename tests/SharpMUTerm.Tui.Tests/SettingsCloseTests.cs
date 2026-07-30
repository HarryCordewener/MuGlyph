using SharpConsoleUI;
using SharpConsoleUI.Configuration;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Drivers;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Tui;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// What closing a settings screen does, driven through <see cref="SettingsOverlay"/> — which is where the
/// reported bug lived, and the reason these tests are at overlay level rather than session level. The
/// session only ever <em>said</em> "close"; the overlay decided that closing meant replaying the screen's
/// undo log.
/// <para>
/// <b>"When I change the address of a world, it does not stick. The moment I go to the main view, it goes
/// back to the old setting."</b> The header hint reads <c>F5/Esc close</c>, and both keys came through
/// <c>Cancel()</c>. So the key the screen advertised as closing it silently discarded every field,
/// checkbox and button press made since it opened, and the F-key — the same key that opened the panel,
/// and the obvious "I'm done here" gesture — was the likeliest way to hit it.
/// </para>
/// <para>
/// Serialised: constructing a <c>ConsoleWindowSystem</c> touches the process-global console streams.
/// </para>
/// </summary>
[NotInParallel]
public class SettingsCloseTests
{
    private const string Host = "aetherfall.mux";

    private static ConsoleKeyInfo Key(ConsoleKey key) => new('\0', key, false, false, false);

    private static ConsoleKeyInfo Char(char c) => new(c, ConsoleKey.NoName, false, false, false);

    private static List<WorldDefinition> Worlds() => new()
    {
        new WorldDefinition
        {
            Name = "Aetherfall",
            Host = Host,
            Characters = new List<CharacterDefinition> { new() { Name = "Corvid" }, new() { Name = "Rookery" } },
        },
        new WorldDefinition { Name = "Grapevine", Host = "grapevine.haus" },
    };

    /// <summary>
    /// An open F5 screen over a real (headless) window system, with the save count the screens drive.
    /// </summary>
    private sealed class Screen
    {
        internal Screen(List<WorldDefinition> worlds, List<TriggerSet>? sets = null)
        {
            Console.SetIn(TextReader.Null);
            var triggerSets = sets ?? new List<TriggerSet>();
            System = new ConsoleWindowSystem(
                new HeadlessConsoleDriver(120, 34), new ConsoleWindowSystemOptions());
            Overlay = new SettingsOverlay(System);
            Session = new SettingsSession(
                selection => WorldsScreenRenderer.Model(
                    worlds,
                    triggerSets,
                    selection.SelectionIn(WorldsScreenRenderer.WorldsPane),
                    selection.SelectionIn(WorldsScreenRenderer.CharactersPane),
                    selection.SelectionIn(WorldsScreenRenderer.TriggerSetsPane)),
                () => Saves++);
        }

        internal ConsoleWindowSystem System { get; }

        internal SettingsOverlay Overlay { get; }

        internal SettingsSession Session { get; }

        internal int Saves { get; private set; }

        /// <summary>Opens the screen on the same path the F5 shortcut takes.</summary>
        internal Screen Open()
        {
            Overlay.Toggle(ConsoleKey.F5, () => new ScreenBinding(
                Session, () => new MarkupControl(new List<string> { "screen" })));
            return this;
        }

        /// <summary>Presses F5 again — the toggle, and the gesture the bug was reported against.</summary>
        internal void PressTheFKey() => Overlay.Toggle(ConsoleKey.F5, () => new ScreenBinding(
            Session, () => new MarkupControl(new List<string> { "screen" })));

        internal void Press(params ConsoleKeyInfo[] keys)
        {
            foreach (var key in keys)
            {
                Overlay.SimulateKey(key);
            }
        }

        /// <summary>Types a new host into the selected world: ⏎ opens its name, ⇥ steps to the host.</summary>
        internal void RetypeTheHost(string host)
        {
            Press(Key(ConsoleKey.Enter), Key(ConsoleKey.Tab));
            for (var i = 0; i < Host.Length; i++)
            {
                Press(Key(ConsoleKey.Backspace));
            }

            foreach (var c in host)
            {
                Press(Char(c));
            }

            Press(Key(ConsoleKey.Enter));
        }
    }

    /// <summary>The reported bug, through the key it was reported against.</summary>
    [Test]
    public async Task AnEditedAddressSurvivesClosingTheScreenWithItsOwnFKey()
    {
        var worlds = Worlds();
        var screen = new Screen(worlds).Open();

        screen.RetypeTheHost("elsewhere.example");
        await Assert.That(worlds[0].Host).IsEqualTo("elsewhere.example");

        screen.PressTheFKey();

        await Assert.That(screen.Overlay.IsOpen).IsFalse();
        await Assert.That(worlds[0].Host).IsEqualTo("elsewhere.example");
    }

    /// <summary>And through Esc, which is the other key the header names.</summary>
    [Test]
    public async Task AnEditedAddressSurvivesClosingTheScreenWithEscape()
    {
        var worlds = Worlds();
        var screen = new Screen(worlds).Open();

        screen.RetypeTheHost("elsewhere.example");
        screen.Press(Key(ConsoleKey.Escape));

        await Assert.That(screen.Overlay.IsOpen).IsFalse();
        await Assert.That(worlds[0].Host).IsEqualTo("elsewhere.example");
    }

    /// <summary>
    /// It is on disk before the screen closes, too. Persisting per committed change is what makes "it
    /// sticks" true across a restart as well as across a close — the alternative, saving on the way out,
    /// turns the same complaint into "it stuck until I restarted".
    /// </summary>
    [Test]
    public async Task EveryCommittedChangeIsPersistedAsItIsMade()
    {
        var worlds = Worlds();
        var screen = new Screen(worlds).Open();

        screen.RetypeTheHost("elsewhere.example");
        var afterField = screen.Saves;

        await Assert.That(afterField).IsGreaterThanOrEqualTo(1);

        // A checkbox too: ← onto the world's security pane, then Space.
        screen.Press(Key(ConsoleKey.RightArrow), Key(ConsoleKey.Spacebar));

        await Assert.That(worlds[0].UseTls).IsTrue();
        await Assert.That(screen.Saves).IsGreaterThan(afterField);
    }

    /// <summary>
    /// A screen that deleted nothing closes instantly. Most closes are this one, and a confirmation on
    /// every one of them is a confirmation people learn to dismiss without reading — which is how somebody
    /// eventually dismisses the one that mattered.
    /// </summary>
    [Test]
    public async Task ACleanScreenClosesWithNoQuestionAsked()
    {
        var screen = new Screen(Worlds()).Open();

        screen.RetypeTheHost("elsewhere.example");
        screen.Press(Key(ConsoleKey.Escape));

        await Assert.That(screen.Overlay.IsOpen).IsFalse();
        await Assert.That(screen.Overlay.Review.IsOpen).IsFalse();
    }

    /// <summary>
    /// A deletion is the one thing that is asked about, because its subject cannot be retyped. The question
    /// names what went, and what went with it.
    /// </summary>
    [Test]
    public async Task DeletingAWorldAndClosingAsksAboutIt()
    {
        var worlds = Worlds();
        var screen = new Screen(worlds).Open();

        screen.Press(Key(ConsoleKey.Delete));
        await Assert.That(worlds.Select(w => w.Name)).IsEquivalentTo(new[] { "Grapevine" });

        screen.Press(Key(ConsoleKey.Escape));

        await Assert.That(screen.Overlay.IsOpen).IsFalse();
        await Assert.That(screen.Overlay.Review.IsOpen).IsTrue();
        await Assert.That(string.Join("\n", screen.Overlay.Review.Lines))
            .Contains("world Aetherfall and its 2 characters");
    }

    /// <summary>
    /// The default answer keeps them, and ⏎ takes the default. A deletion took a deliberate Delete on a
    /// deliberately chosen row; the prompt is a review, not a second guess, so the answer that respects the
    /// work is the one standing under ⏎.
    /// </summary>
    [Test]
    public async Task TheDefaultAnswerKeepsTheDeletions()
    {
        var worlds = Worlds();
        var screen = new Screen(worlds).Open();

        screen.Press(Key(ConsoleKey.Delete), Key(ConsoleKey.Escape));
        await Assert.That(string.Join("\n", screen.Overlay.Review.Lines)).Contains("⏎ keep");

        screen.Overlay.Review.SimulateKey(Key(ConsoleKey.Enter));

        await Assert.That(screen.Overlay.Review.IsOpen).IsFalse();
        await Assert.That(worlds.Select(w => w.Name)).IsEquivalentTo(new[] { "Grapevine" });
    }

    /// <summary>
    /// And <c>n</c> puts them back — the world at its own index, so the list the screen navigates by is the
    /// list it was. Both answers close: Esc was a navigation gesture, and turning it into "actually, stay
    /// here" would answer a question the user did not ask.
    /// </summary>
    [Test]
    public async Task AnsweringUndoRestoresTheDeletedWorldAtItsIndex()
    {
        var worlds = Worlds();
        var screen = new Screen(worlds).Open();

        screen.Press(Key(ConsoleKey.Delete), Key(ConsoleKey.Escape));
        screen.Overlay.Review.SimulateKey(Char('n'));

        await Assert.That(screen.Overlay.Review.IsOpen).IsFalse();
        await Assert.That(screen.Overlay.IsOpen).IsFalse();
        await Assert.That(worlds.Select(w => w.Name)).IsEquivalentTo(new[] { "Aetherfall", "Grapevine" });
        await Assert.That(worlds[0].Characters.Select(c => c.Name)).IsEquivalentTo(new[] { "Corvid", "Rookery" });
    }

    /// <summary>
    /// Several deletions are one question, named individually. A per-press confirmation would have asked
    /// three times to do what the user was plainly in the middle of doing.
    /// </summary>
    [Test]
    public async Task SeveralDeletionsAreReviewedTogether()
    {
        var worlds = Worlds();
        var screen = new Screen(worlds).Open();

        screen.Press(Key(ConsoleKey.Delete), Key(ConsoleKey.Delete));
        await Assert.That(worlds).IsEmpty();

        screen.Press(Key(ConsoleKey.Escape));

        var asked = string.Join("\n", screen.Overlay.Review.Lines);
        await Assert.That(asked).Contains("world Aetherfall and its 2 characters");
        await Assert.That(asked).Contains("world Grapevine");

        screen.Overlay.Review.SimulateKey(Char('n'));

        await Assert.That(worlds.Select(w => w.Name)).IsEquivalentTo(new[] { "Aetherfall", "Grapevine" });
    }

    /// <summary>
    /// A committed edit made <em>beside</em> a deletion is not undone by answering "put them back". The
    /// review's scope is exactly the deletions it named — anything wider would be the bug again, reached
    /// through the one prompt that is allowed to undo anything.
    /// </summary>
    [Test]
    public async Task AnsweringUndoDoesNotTouchCommittedEdits()
    {
        var worlds = Worlds();
        var screen = new Screen(worlds).Open();

        screen.RetypeTheHost("elsewhere.example");
        screen.Press(Key(ConsoleKey.DownArrow), Key(ConsoleKey.Delete)); // the second world
        await Assert.That(worlds.Select(w => w.Name)).IsEquivalentTo(new[] { "Aetherfall" });

        screen.Press(Key(ConsoleKey.Escape));
        screen.Overlay.Review.SimulateKey(Char('n'));

        await Assert.That(worlds.Select(w => w.Name)).IsEquivalentTo(new[] { "Aetherfall", "Grapevine" });
        await Assert.That(worlds[0].Host).IsEqualTo("elsewhere.example");
    }

    /// <summary>
    /// Closing while a field is open drops the <em>buffer</em> and keeps everything that was committed —
    /// which is the scope rule extended one step: leaving discards what was never confirmed, and a buffer
    /// is unconfirmed by definition. It is the same answer the inner Esc gives, so the two cannot disagree.
    /// <para>
    /// The alternative — commit the open field on the way out — was considered and is worse. It would let
    /// the panel's toggle key <em>write</em> a value the user had not finished typing: <c>elsewh</c> as a
    /// host, persisted, on a screen whose whole complaint was about values changing without being asked
    /// for. Half a hostname is not a hostname; a confirmed one is confirmed by ⏎.
    /// </para>
    /// </summary>
    [Test]
    public async Task ClosingWithAFieldOpenDropsTheBufferAndKeepsWhatWasCommitted()
    {
        var worlds = Worlds();
        var screen = new Screen(worlds).Open();

        screen.RetypeTheHost("elsewhere.example"); // committed, with ⏎

        // Now open the host again and type without confirming.
        screen.Press(Key(ConsoleKey.Enter), Key(ConsoleKey.Tab));
        foreach (var c in "-half")
        {
            screen.Press(Char(c));
        }

        await Assert.That(screen.Session.IsEditing).IsTrue();
        screen.PressTheFKey();

        await Assert.That(screen.Overlay.IsOpen).IsFalse();
        await Assert.That(worlds[0].Host).IsEqualTo("elsewhere.example");
    }

    /// <summary>
    /// The other F-key: F9 opens the same screen on the character pane, and pressing it while F5's own
    /// screen is up is a <em>re-open</em>, not a close. It must not discard anything either.
    /// </summary>
    [Test]
    public async Task ReOpeningOnAnotherFKeyKeepsWhatWasCommitted()
    {
        var worlds = Worlds();
        var screen = new Screen(worlds).Open();

        screen.RetypeTheHost("elsewhere.example");
        screen.Overlay.Toggle(ConsoleKey.F9, () => new ScreenBinding(
            screen.Session, () => new MarkupControl(new List<string> { "screen" })));

        await Assert.That(screen.Overlay.IsOpen).IsTrue();
        await Assert.That(screen.Overlay.OpenKey).IsEqualTo(ConsoleKey.F9);
        await Assert.That(worlds[0].Host).IsEqualTo("elsewhere.example");
    }
}
