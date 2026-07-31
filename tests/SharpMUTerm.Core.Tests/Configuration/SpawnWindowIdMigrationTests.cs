using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Workspaces;

namespace SharpMUTerm.Core.Tests.Configuration;

/// <summary>
/// The v4 → v5 upgrade: spawn window ids gain their owner, so two characters capturing one target stop
/// sharing a pane. The whole risk of the change is on this seam — every existing installation's
/// <c>config.json</c> holds <c>spawn:Public</c> rows, and both the pane placement in <c>lastSession</c>
/// and the scrollback in the restore log are keyed by those exact strings.
/// <para>
/// <b>The decision, and why it is the one to make.</b> An old id is <em>migrated</em>, not adopted and
/// not dropped. Adoption — leaving the id alone and letting whichever session claims the target keep it
/// — cannot work, because nothing the running client now produces equals <c>spawn:Public</c>: the pane
/// would come back and then sit there for ever with nothing writing to it while its channel filled a
/// second pane beside it. Dropping is honest and throws away a pane the user had and, through the id
/// the restore log is keyed by, the text in it. Migrating keeps the pane, its place in the split tree
/// and its content, and produces exactly the id the session that owned it will route to.
/// </para>
/// <para>
/// <b>What supplies the owner is the saved state itself.</b> <c>WorkspaceWindowState.SessionKey</c> has
/// been persisted since spawn windows existed, so an old document already records which character each
/// pane belonged to; the rewrite is a lookup and not a guess.
/// </para>
/// </summary>
public class SpawnWindowIdMigrationTests
{
    /// <summary>A v4 document: one character, a main window and a Chat spawn beside it in one pane.</summary>
    private const string V4 = """
    {
      "version": 4,
      "worlds": [ { "name": "Aetherfall", "host": "aetherfall.mux", "port": 4201,
        "characters": [ { "name": "Corvid" } ] } ],
      "lastSession": {
        "windows": [
          { "id": "main", "title": "Corvid", "kind": "Main", "sessionKey": "Aetherfall.Corvid" },
          { "id": "spawn:Chat", "title": "Chat", "kind": "Spawn", "sessionKey": "Aetherfall.Corvid",
            "ownerLabel": "Corvid", "capturePattern": "^\\[Chat\\]" }
        ],
        "root": { "type": "pane", "id": "p1", "tabs": [ "main", "spawn:Chat" ], "activeIndex": 0 },
        "focusedPaneId": "p1"
      }
    }
    """;

    /// <summary>The id the running client will route Corvid's <c>Chat</c> capture to.</summary>
    private static string CorvidsChat => Workspace.SpawnWindowId("Aetherfall.Corvid", "Chat");

    /// <summary>
    /// The headline: the saved pane keeps everything it had, under the id the session that owns it now
    /// produces. Placement included — the rewrite has to reach the pane's <c>tabs</c> array as well as
    /// the window row, or the workspace comes back with a window in no pane and a pane naming no window.
    /// </summary>
    [Test]
    public async Task AV4SpawnWindowKeepsItsPaneItsPlaceAndItsMetadata()
    {
        var config = ConfigurationStore.Deserialize(V4);

        await Assert.That(config.Version).IsEqualTo(AppConfiguration.CurrentVersion);

        var window = config.LastSession!.Windows.Single(w => w.Kind == WindowKind.Spawn);
        await Assert.That(window.Id).IsEqualTo(CorvidsChat);
        await Assert.That(window.Title).IsEqualTo("Chat");
        await Assert.That(window.SessionKey).IsEqualTo("Aetherfall.Corvid");
        await Assert.That(window.OwnerLabel).IsEqualTo("Corvid");

        // There is no CapturePattern to assert any more: the capture header it fed was removed with the
        // spawn pane's dim "⇱ capture …" row, and the field went with it. A v4 document may still carry
        // the property — the fixture below does — and System.Text.Json drops what it cannot map, which is
        // the behaviour that lets an old config load at all.

        await Assert.That(config.LastSession.Root.Tabs).IsEquivalentTo(new[] { "main", CorvidsChat });
    }

    /// <summary>
    /// <b>The property the migration exists for.</b> Every window the resumed workspace holds is one the
    /// running client can route to: the id of each spawn pane is exactly what
    /// <see cref="Workspace.RouteSpawn"/> produces for the owner and target that pane records. A pane
    /// left under a v4 id would fail this — and that failure, on a live client, is a pane nobody ever
    /// writes to again.
    /// </summary>
    [Test]
    public async Task NoResumedPaneIsLeftWithAnIdNothingRoutesTo()
    {
        var workspace = ConfigurationStore.Deserialize(V4).LastSession!.Restore();

        foreach (var window in workspace.Windows.Where(w => w.Kind == WindowKind.Spawn).ToList())
        {
            var routed = workspace.RouteSpawn(window.Title, window.SessionKey);
            await Assert.That(routed.Id).IsEqualTo(window.Id);
        }

        // …and routing them did not open anything new beside them.
        await Assert.That(workspace.Windows.Count).IsEqualTo(2);
        await Assert.That(workspace.Layout.FindWindow(CorvidsChat)).IsNotNull();
    }

    /// <summary>
    /// A v4 spawn window that recorded no owner becomes the unowned form, which is the id the client
    /// produces for a spawn window nobody owns — so it stays reachable rather than becoming an orphan by
    /// a different route.
    /// </summary>
    [Test]
    public async Task AV4SpawnWindowWithNoOwnerBecomesTheUnownedId()
    {
        var config = ConfigurationStore.Deserialize("""
        {
          "version": 4,
          "lastSession": {
            "windows": [ { "id": "spawn:Notes", "title": "Notes", "kind": "Spawn" } ],
            "root": { "type": "pane", "id": "p1", "tabs": [ "spawn:Notes" ], "activeIndex": 0 },
            "focusedPaneId": "p1"
          }
        }
        """);

        await Assert.That(config.LastSession!.Windows.Single().Id)
            .IsEqualTo(Workspace.SpawnWindowId(null, "Notes"));
    }

    /// <summary>The rewrite reaches panes nested in a split, not only the root one.</summary>
    [Test]
    public async Task TabsAreRewrittenInsideASplitToo()
    {
        var config = ConfigurationStore.Deserialize("""
        {
          "version": 4,
          "lastSession": {
            "windows": [
              { "id": "main", "title": "Corvid", "kind": "Main", "sessionKey": "Aetherfall.Corvid" },
              { "id": "spawn:Chat", "title": "Chat", "kind": "Spawn", "sessionKey": "Aetherfall.Corvid" }
            ],
            "root": { "type": "split", "direction": "Row", "sizes": [ 0.5, 0.5 ], "children": [
              { "type": "pane", "id": "p1", "tabs": [ "main" ], "activeIndex": 0 },
              { "type": "pane", "id": "p2", "tabs": [ "spawn:Chat" ], "activeIndex": 0 }
            ] },
            "focusedPaneId": "p1"
          }
        }
        """);

        var workspace = config.LastSession!.Restore();
        await Assert.That(workspace.Layout.FindWindow(CorvidsChat)!.Id).IsEqualTo("p2");
    }

    /// <summary>
    /// Only spawn windows move. The main window and any auxiliary keep the ids they had, because those
    /// were never derived from a target and nothing about them was ambiguous.
    /// </summary>
    [Test]
    public async Task NonSpawnWindowsAreLeftAlone()
    {
        var config = ConfigurationStore.Deserialize("""
        {
          "version": 4,
          "lastSession": {
            "windows": [
              { "id": "main", "title": "Corvid", "kind": "Main", "sessionKey": "Aetherfall.Corvid" },
              { "id": "web", "title": "Page", "kind": "Auxiliary" }
            ],
            "root": { "type": "pane", "id": "p1", "tabs": [ "main", "web" ], "activeIndex": 0 },
            "focusedPaneId": "p1"
          }
        }
        """);

        await Assert.That(config.LastSession!.Windows.Select(w => w.Id)).IsEquivalentTo(new[] { "main", "web" });
        await Assert.That(config.LastSession.Root.Tabs).IsEquivalentTo(new[] { "main", "web" });
    }

    /// <summary>
    /// The step is idempotent in the way that matters: a document already at the current version is not
    /// re-encoded. Running the rewrite twice would wrap one id inside another
    /// (<c>spawn:22:…:spawn:17:…:Chat</c>) and lose the pane a second way, so this is the assertion that
    /// keeps the version gate honest.
    /// </summary>
    [Test]
    public async Task ADocumentAlreadyAtTheCurrentVersionIsNotRewrittenAgain()
    {
        var once = ConfigurationStore.Deserialize(V4);
        var twice = ConfigurationStore.Deserialize(ConfigurationStore.Serialize(once));

        await Assert.That(twice.LastSession!.Windows.Single(w => w.Kind == WindowKind.Spawn).Id)
            .IsEqualTo(CorvidsChat);
        await Assert.That(twice.LastSession.Root.Tabs).IsEquivalentTo(new[] { "main", CorvidsChat });
    }

    /// <summary>
    /// A v4 document with no saved session at all — the overwhelmingly common shape of an old file — is
    /// untouched apart from its version, and starting from it does not throw.
    /// </summary>
    [Test]
    public async Task AV4DocumentWithNoSavedSessionIsFine()
    {
        var config = ConfigurationStore.Deserialize("""
        { "version": 4, "worlds": [ { "name": "Aetherfall", "host": "aetherfall.mux" } ] }
        """);

        await Assert.That(config.LastSession).IsNull();
        await Assert.That(config.Version).IsEqualTo(AppConfiguration.CurrentVersion);
    }

    /// <summary>
    /// And a hand-edited document does not make the <em>migrator</em> throw. It is asserted at that
    /// level rather than through <see cref="ConfigurationStore.Deserialize"/> because this step runs
    /// before deserialization: a number where a window id belongs, a missing <c>kind</c>, a tabs array
    /// with a number in it — all things <c>GetValue&lt;string&gt;</c> throws on and this reads as
    /// "not a string, leave it alone".
    /// </summary>
    [Test]
    public async Task AMangledSavedSessionDoesNotStopTheMigrator()
    {
        var root = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""
        {
          "version": 4,
          "lastSession": {
            "windows": [ { "id": 7, "kind": 1 }, { "id": "spawn:Chat" }, { "kind": "Spawn" },
                         { "id": "spawn:Chat", "kind": "Spawn" } ],
            "root": { "type": "pane", "id": "p1", "tabs": [ 3, "spawn:Chat" ], "activeIndex": 0 },
            "focusedPaneId": "p1"
          }
        }
        """)!;

        ConfigurationMigrator.Migrate(root);

        await Assert.That(root["version"]!.GetValue<int>()).IsEqualTo(AppConfiguration.CurrentVersion);

        // The one row that was a spawn window with a readable id did move, and the tab moved with it.
        var tabs = (System.Text.Json.Nodes.JsonArray)root["lastSession"]!["root"]!["tabs"]!;
        await Assert.That(tabs[1]!.GetValue<string>()).IsEqualTo(Workspace.SpawnWindowId(null, "Chat"));
    }
}
