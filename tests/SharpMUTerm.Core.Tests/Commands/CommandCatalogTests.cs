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

    [Test]
    public async Task Catalog_CoversAllFourGroups()
    {
        var ws = new Workspace();
        var catalog = CommandCatalog.Build(ws, Characters, "Aetherfall.Corvid", new CommandContext());
        foreach (var group in Enum.GetValues<CommandGroup>())
        {
            await Assert.That(catalog.Any(c => c.Group == group)).IsTrue();
        }
    }
}
