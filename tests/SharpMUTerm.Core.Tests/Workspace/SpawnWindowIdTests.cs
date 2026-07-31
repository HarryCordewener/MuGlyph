using SharpMUTerm.Core.Workspaces;

namespace SharpMUTerm.Core.Tests.Workspaces;

/// <summary>
/// The identity a capture pane is filed under. It has to be unique per <c>(owner, target)</c> — that is
/// the whole of the fix for two characters sharing one <c>Public</c> window — and stable for ever, since
/// it keys the workspace registry, a value in <c>config.json</c>, and a <c>RestoreLog</c> file name.
/// <para>
/// The pressure this suite is really under is that <b>both halves are user-controlled free text</b>: a
/// world or a character may be called anything, and a trigger's <c>SpawnTarget</c> is whatever was typed
/// on F2. So "unique" cannot mean "unique for the names people usually pick" — the encoding has to be
/// injective over every pair, which is what the length prefix buys and what a plain <c>owner:target</c>
/// join does not.
/// </para>
/// </summary>
public class SpawnWindowIdTests
{
    /// <summary>The ordinary case reads as what it is, which is why this is an encoding and not a digest.</summary>
    [Test]
    public async Task AnIdNamesItsOwnerAndItsTarget()
    {
        await Assert.That(Workspace.SpawnWindowId("Aetherfall.Corvid", "Chat"))
            .IsEqualTo("spawn:17:Aetherfall.Corvid:Chat");
        await Assert.That(Workspace.SpawnWindowId(null, "Chat")).IsEqualTo("spawn:-:Chat");
    }

    /// <summary>Two characters, one capture rule, two windows. The report, at the level it is caused.</summary>
    [Test]
    public async Task TwoOwnersOfOneTargetGetTwoIds()
    {
        await Assert.That(Workspace.SpawnWindowId("Convergence.Ann", "Public"))
            .IsNotEqualTo(Workspace.SpawnWindowId("Convergence.Bob", "Public"));
    }

    /// <summary>
    /// <b>The pair that a separator-joined id would collapse.</b> <c>("a", "b:c")</c> and
    /// <c>("a:b", "c")</c> are two different windows — a character called <c>a</c> capturing a channel
    /// called <c>b:c</c>, and a character called <c>a:b</c> capturing one called <c>c</c> — and
    /// <c>$"spawn:{owner}:{target}"</c> spells both <c>spawn:a:b:c</c>. That is this very defect in a
    /// rarer shape, and it is why the owner's length is written in front of it.
    /// </summary>
    [Test]
    public async Task ColonsInEitherHalfDoNotCollapseTwoWindowsIntoOne()
    {
        await Assert.That(Workspace.SpawnWindowId("a", "b:c")).IsNotEqualTo(Workspace.SpawnWindowId("a:b", "c"));
    }

    /// <summary>
    /// And the property behind those examples, over a spread of names chosen to be awkward: distinct
    /// pairs give distinct ids, every time. A table of cases can only ever say "not these"; this says
    /// "none of them", which is the claim the design actually rests on.
    /// </summary>
    [Test]
    public async Task DistinctPairsAlwaysGiveDistinctIds()
    {
        string?[] owners = [null, "", "a", "a:b", "1", "12:x", "-", "-:x", "World.Char", "spawn:", ":", "::"];
        string[] targets = ["Chat", "a", "b:c", "c", ":", "spawn:Chat", "-:Chat", "17:Aetherfall.Corvid:Chat", "1"];

        var seen = new Dictionary<string, (string? Owner, string Target)>(StringComparer.Ordinal);
        foreach (var owner in owners)
        {
            foreach (var target in targets)
            {
                var id = Workspace.SpawnWindowId(owner, target);
                await Assert.That(seen.TryAdd(id, (owner, target)))
                    .IsTrue()
                    .Because($"({owner ?? "null"}, {target}) collided with {seen.GetValueOrDefault(id)} on {id}");
            }
        }
    }

    /// <summary>
    /// The id is readable back, which is what lets the restore log carry a pre-owner file onto the pane
    /// that now holds its channel. Over the same awkward spread, so the reader is exercised on the
    /// inputs that would break a naive split.
    /// </summary>
    [Test]
    public async Task AnIdReadsBackAsThePairThatMadeIt()
    {
        string?[] owners = [null, "", "a:b", "-", "17:Aetherfall.Corvid", "World.Char"];
        string[] targets = ["Chat", "b:c", ":", "-:Chat", "spawn:Chat"];

        foreach (var owner in owners)
        {
            foreach (var target in targets)
            {
                var id = Workspace.SpawnWindowId(owner, target);
                await Assert.That(Workspace.TryReadSpawnWindowId(id, out var readOwner, out var readTarget)).IsTrue();
                await Assert.That(readOwner).IsEqualTo(owner);
                await Assert.That(readTarget).IsEqualTo(target);
            }
        }
    }

    /// <summary>
    /// A window id from before the owner was in it does <em>not</em> read as one of these, which is what
    /// lets the restore log tell an old file apart from a current one without being told. (The
    /// configuration is not left to work it out by shape — it goes by the document's schema version —
    /// because a target is free text and may be made to look like anything, including this.)
    /// </summary>
    [Test]
    [Arguments("spawn:Chat")]
    [Arguments("spawn:Public")]
    [Arguments("spawn:")]
    [Arguments("main")]
    [Arguments("spawn:99:short:x")]
    [Arguments("spawn:+3:abc:x")]
    [Arguments("spawn: 3:abc:x")]
    public async Task WhatIsNotOneOfTheseIsRefused(string id)
    {
        await Assert.That(Workspace.TryReadSpawnWindowId(id, out _, out _)).IsFalse();
    }

    /// <summary>A spawn window has to be called something; an empty target is not an id.</summary>
    [Test]
    public async Task AnEmptyTargetIsRefused()
    {
        await Assert.That(() => Workspace.SpawnWindowId("Aetherfall.Corvid", string.Empty))
            .Throws<ArgumentException>();
    }

    /// <summary>
    /// The workspace routes by that identity: one target, two sessions, two windows, each owned by the
    /// session whose rule fired, and neither holding the other's activity.
    /// </summary>
    [Test]
    public async Task RouteSpawnGivesEachSessionItsOwnWindow()
    {
        var workspace = new Workspace(sessionKey: "Convergence.Ann");

        var ann = workspace.RouteSpawn("Public", "Convergence.Ann");
        var bob = workspace.RouteSpawn("Public", "Convergence.Bob");

        await Assert.That(ann).IsNotEqualTo(bob);
        await Assert.That(ann.SessionKey).IsEqualTo("Convergence.Ann");
        await Assert.That(bob.SessionKey).IsEqualTo("Convergence.Bob");
        await Assert.That(ann.Title).IsEqualTo("Public");
        await Assert.That(bob.Title).IsEqualTo("Public");
        await Assert.That(ann.Unread).IsEqualTo(1);
        await Assert.That(bob.Unread).IsEqualTo(1);
        await Assert.That(workspace.Windows.Count).IsEqualTo(3); // main + one each
    }

    /// <summary>
    /// And the same session routing twice still lands in one window — the property that makes the id a
    /// <em>name</em> rather than a fresh identity per line.
    /// </summary>
    [Test]
    public async Task OneSessionRoutingTwiceKeepsOneWindow()
    {
        var workspace = new Workspace(sessionKey: "Convergence.Ann");

        var first = workspace.RouteSpawn("Public", "Convergence.Ann");
        var second = workspace.RouteSpawn("Public", "Convergence.Ann");

        await Assert.That(second).IsEqualTo(first);
        await Assert.That(workspace.Windows.Count).IsEqualTo(2);
    }

    /// <summary>
    /// <b>Windows nobody owns share one bucket, and that is right rather than an oversight.</b> A live
    /// <c>WorldSession</c> always has a key (its world's name when it has no character), so an unowned
    /// spawn window cannot come from a connection at all. And the rail lists an unowned window under
    /// <em>every</em> character precisely because it belongs to none — so two of them for one target
    /// would draw two identical rows under everybody, with nothing in the client able to tell them apart
    /// or route between them. Null is one identity, "nobody", and not an unknown owner.
    /// </summary>
    [Test]
    public async Task WindowsWithNoOwnerAreOneWindowPerTarget()
    {
        var workspace = new Workspace();

        var first = workspace.RouteSpawn("Public");
        var second = workspace.RouteSpawn("Public");

        await Assert.That(second).IsEqualTo(first);
        await Assert.That(first.SessionKey).IsNull();

        // …and an unowned window is still nobody's, so it does not collide with an owned one.
        await Assert.That(workspace.RouteSpawn("Public", "Convergence.Ann")).IsNotEqualTo(first);
    }
}
