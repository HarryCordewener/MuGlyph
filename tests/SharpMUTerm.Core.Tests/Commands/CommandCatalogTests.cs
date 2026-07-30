using SharpMUTerm.Core.Commands;
using SharpMUTerm.Core.Workspaces;

namespace SharpMUTerm.Core.Tests.Commands;

public class CommandCatalogTests
{
    private static readonly CharacterRef[] Characters =
    {
        new("Aetherfall", "Corvid", "Aetherfall.Corvid", Connected: true),
        new("Aetherfall", "Rookery", "Aetherfall.Rookery", Connected: false),
    };

    [Test]
    public async Task NonFocusedCharacters_BecomeSwitchEntries()
    {
        var ws = new Workspace();
        var catalog = CommandCatalog.Build(ws, Characters, "Aetherfall.Corvid", new CommandContext());

        var switches = catalog.Where(c => c.Id.StartsWith("char:")).ToArray();
        await Assert.That(switches).HasSingleItem(); // Corvid is focused, only Rookery remains
        await Assert.That(switches[0].Title).IsEqualTo("Switch to Rookery");
        await Assert.That(switches[0].Subtitle).Contains("offline");
    }

    [Test]
    public async Task NonActiveWindows_BecomeGoToEntries_WithOwnerAndUnread()
    {
        var ws = new Workspace();
        ws.RouteSpawn("Chat"); // background window, 1 unread, owner unset here

        var catalog = CommandCatalog.Build(ws, Characters, "Aetherfall.Corvid", new CommandContext());
        var goTo = catalog.Single(c => c.Id.StartsWith("win:"));
        await Assert.That(goTo.Title).IsEqualTo("Go to Chat");
        await Assert.That(goTo.Subtitle).Contains("1 unread");
    }

    [Test]
    public async Task StatefulCommands_ReadCurrentValue()
    {
        var ws = new Workspace();
        var loggingOff = CommandCatalog.Build(ws, Characters, null, new CommandContext(LoggingOn: false));
        await Assert.That(loggingOff.Any(c => c.Title == "Start logging")).IsTrue();

        var loggingOn = CommandCatalog.Build(ws, Characters, null, new CommandContext(LoggingOn: true, Zoomed: true, Frozen: true));
        await Assert.That(loggingOn.Any(c => c.Title == "Pause logging")).IsTrue();
        await Assert.That(loggingOn.Any(c => c.Title == "Unzoom pane")).IsTrue();
        await Assert.That(loggingOn.Any(c => c.Title == "Resume scrollback")).IsTrue();
    }

    /// <summary>
    /// The numbered pane entries: one per pane that exists, in <c>Panes</c> order (which is the order the
    /// shell's sidebar numbers them in), and only when there is more than one pane. The first nine carry
    /// their chord; a tenth pane has none, and an entry naming a key that does something else would be
    /// worse than a bare one.
    /// </summary>
    [Test]
    public async Task NumberedPaneEntries_AppearOnlyOnASplit_AndOnlyTheFirstNineCarryAChord()
    {
        var one = CommandCatalog.Build(new Workspace(), Characters, null, new CommandContext());
        await Assert.That(one.Any(c => c.Id.StartsWith(CommandIds.PanePrefix, StringComparison.Ordinal)))
            .IsFalse();

        var ws = new Workspace();
        for (var i = 0; i < 10; i++)
        {
            ws.RouteSpawn($"w{i}");
        }

        // Ten panes. A split moves the focused pane's *other* tabs into the new one and leaves focus
        // where it was, so the pane still holding a pile of tabs is the newest — focus that one to split
        // again, or the second split has nothing to pull out.
        while (ws.Layout.Panes.Count <= CommandIds.PaneJumpDigits)
        {
            ws.Layout.Focus(ws.Layout.Panes[^1].Id);
            if (!ws.Layout.SplitFocused(SplitDirection.Row))
            {
                break;
            }
        }

        var panes = ws.Layout.Panes.Count;
        await Assert.That(panes).IsGreaterThan(CommandIds.PaneJumpDigits);

        var entries = CommandCatalog.Build(ws, Characters, null, new CommandContext())
            .Where(c => c.Id.StartsWith(CommandIds.PanePrefix, StringComparison.Ordinal))
            .ToList();

        await Assert.That(entries.Count).IsEqualTo(panes);
        for (var n = 1; n <= panes; n++)
        {
            var entry = entries[n - 1];
            await Assert.That(entry.Id).IsEqualTo(CommandIds.Pane(n));
            await Assert.That(entry.Title).IsEqualTo($"Go to pane {n}");
            await Assert.That(entry.Subtitle).IsEqualTo(n <= CommandIds.PaneJumpDigits ? $"⌥{n}" : null);
        }
    }

    [Test]
    public async Task Catalog_CoversEveryGroup()
    {
        var ws = new Workspace();
        var catalog = CommandCatalog.Build(ws, Characters, "Aetherfall.Corvid", new CommandContext(), Screens);
        foreach (var group in Enum.GetValues<CommandGroup>())
        {
            await Assert.That(catalog.Any(c => c.Group == group)).IsTrue();
        }
    }

    private static readonly SettingsEntry[] Screens =
    {
        new("Worlds & Characters", "screen:worlds", "F5"),
        new("Aliases", "screen:aliases", "F3"),
    };

    [Test]
    public async Task EverySettingsScreenTheHostOffers_BecomesAnEntry_SubtitledWithItsKey()
    {
        var ws = new Workspace();
        var catalog = CommandCatalog.Build(ws, Characters, null, new CommandContext(), Screens);

        var settings = catalog.Where(c => c.Group == CommandGroup.Settings).ToArray();
        await Assert.That(settings.Length).IsEqualTo(2);
        await Assert.That(settings[0].Title).IsEqualTo("Open Worlds & Characters");
        await Assert.That(settings[0].Id).IsEqualTo("screen:worlds");
        await Assert.That(settings[0].Subtitle).IsEqualTo("F5"); // the palette teaches the shortcut
        // The host's order is kept: these are its F-keys, and re-sorting them would hide that.
        await Assert.That(settings[1].Id).IsEqualTo("screen:aliases");
    }

    [Test]
    public async Task AHostThatOffersNoScreens_GetsNoSettingsGroup()
    {
        var ws = new Workspace();
        var catalog = CommandCatalog.Build(ws, Characters, null, new CommandContext());

        await Assert.That(catalog.Any(c => c.Group == CommandGroup.Settings)).IsFalse();
    }
}
