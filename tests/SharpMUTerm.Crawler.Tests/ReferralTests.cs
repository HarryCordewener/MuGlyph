using SharpMUTerm.Core.Telnet.Mssp;
using SharpMUTerm.Crawler;
using SharpMUTerm.Crawler.Model;
using SharpMUTerm.Crawler.Scheduling;
using SharpMUTerm.Crawler.Tests.Support;

namespace SharpMUTerm.Crawler.Tests;

/// <summary>
/// <c>REFERRAL</c>: the format the specification defines, and what a crawler does with what it finds.
/// </summary>
public class ReferralTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static MsspHost Host(string value)
    {
        MsspHost.TryParse(value, out var host);
        return host!;
    }

    [Test]
    public async Task TheSpecifiedFormatIsHostSpacePort()
    {
        // "using the host port format … Make sure to separate the host and port with a space rather
        // than : because IPv6 addresses contain colons."
        MsspHost.TryParse("mud.example.org 4000", out var host);

        await Assert.That(host).IsNotNull();
        await Assert.That(host!.Host).IsEqualTo("mud.example.org");
        await Assert.That(host.Port).IsEqualTo(4000);
        await Assert.That(host.ToReferralString()).IsEqualTo("mud.example.org 4000");
    }

    [Test]
    public async Task AnIpV6ReferralIsParsedAndCanonicalised()
    {
        // The exact reason the specification chose a space. Two spellings of one address must become one
        // host, or a crawl visits it once per spelling its peers happen to use.
        MsspHost.TryParse("2001:0DB8:0000:0000:0000:0000:0000:0001 4201", out var verbose);
        MsspHost.TryParse("2001:db8::1 4201", out var compact);

        await Assert.That(verbose).IsNotNull();
        await Assert.That(verbose!.Host).IsEqualTo("2001:db8::1");
        await Assert.That(verbose).IsEqualTo(compact!);
        await Assert.That(verbose.ToString()).IsEqualTo("[2001:db8::1]:4201");
    }

    [Test]
    public async Task ABracketedIpV6LiteralIsAccepted()
    {
        MsspHost.TryParse("[2001:db8::1] 4201", out var host);

        await Assert.That(host).IsNotNull();
        await Assert.That(host!.Host).IsEqualTo("2001:db8::1");
    }

    [Test]
    public async Task TheColonFormIsToleratedOnlyWhereItCannotBeAnIpV6Address()
    {
        // Real servers emit host:port despite the specification. Accepting it recovers referrals that
        // would otherwise be lost…
        MsspHost.TryParse("mud.example.org:4000", out var name);
        await Assert.That(name).IsNotNull();
        await Assert.That(name!.Port).IsEqualTo(4000);

        // …but a string with more than one colon is far more likely to be a bare IPv6 address, and
        // splitting it at the last colon would silently rewrite it into a different address.
        await Assert.That(MsspHost.TryParse("2001:db8::1", out _)).IsFalse();
        await Assert.That(MsspHost.TryParse("2001:db8::1:4201", out _)).IsFalse();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("mud.example.org")]
    [Arguments("mud.example.org 0")]
    [Arguments("mud.example.org 70000")]
    [Arguments("mud.example.org -1")]
    [Arguments("mud.example.org notaport")]
    [Arguments("http://mud.example.org/ 4000")]
    [Arguments("a stale line someone typed")]
    public async Task AMalformedReferralIsRejectedRatherThanGuessedAt(string value) =>
        await Assert.That(MsspHost.TryParse(value, out _)).IsFalse();

    [Test]
    public async Task HostNamesAreNormalisedSoOneServerIsOneEntry()
    {
        var spellings = new[]
        {
            "MUD.Example.ORG 4201",
            "mud.example.org 4201",
            "mud.example.org. 4201",
            "mud.example.org:4201",
        };

        var hosts = spellings.Select(Host).ToHashSet();
        await Assert.That(hosts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task TheReferralListIsReadFromTheArrayAndDeduplicated()
    {
        var parser = new MsspSubnegotiationParser();
        var data = parser.Consume(MsspWire.Subnegotiation(
            ("REFERRAL",
            [
                "a.example.org 4000",
                "b.example.org 4000",
                "A.EXAMPLE.ORG 4000",     // the same host, spelled differently
                "not a referral",          // a stale line; dropped, not fatal
                "2001:db8::9 4201",
            ]))).Single();

        await Assert.That(data.Referrals.Select(r => r.ToReferralString()))
            .IsEquivalentTo(new[] { "a.example.org 4000", "b.example.org 4000", "2001:db8::9 4201" });

        // The raw strings stay readable, so a report can show what a server actually said.
        await Assert.That(data["REFERRAL"].Count).IsEqualTo(5);
    }

    [Test]
    public async Task PrivateAndLoopbackAddressesAreClassifiedAsUncrawlable()
    {
        await Assert.That(Host("127.0.0.1 4201").Scope).IsEqualTo(MsspHostScope.Loopback);
        await Assert.That(Host("::1 4201").Scope).IsEqualTo(MsspHostScope.Loopback);
        await Assert.That(Host("10.1.2.3 4201").Scope).IsEqualTo(MsspHostScope.Private);
        await Assert.That(Host("192.168.1.1 4201").Scope).IsEqualTo(MsspHostScope.Private);
        await Assert.That(Host("172.16.0.1 4201").Scope).IsEqualTo(MsspHostScope.Private);
        await Assert.That(Host("fd00::1 4201").Scope).IsEqualTo(MsspHostScope.Private);

        // The cloud metadata address, which is the one that matters.
        await Assert.That(Host("169.254.169.254 80").Scope).IsEqualTo(MsspHostScope.LinkLocal);

        await Assert.That(Host("198.51.100.7 4201").Scope).IsEqualTo(MsspHostScope.Global);
        await Assert.That(Host("mud.example.org 4201").Scope).IsEqualTo(MsspHostScope.Unresolved);
    }

    // ---- What the frontier does with them ----

    private static CrawlFrontier Frontier(CrawlOptions? options = null) =>
        new(options ?? new CrawlOptions());

    [Test]
    public async Task AReferralBackToTheReferrerIsRecognisedAndIgnored()
    {
        // The simplest cycle, and the commonest: two servers list each other. Neither is a fault.
        var frontier = Frontier();
        var a = Host("a.example.org 4000");
        var b = Host("b.example.org 4000");

        frontier.AddSeed(a, Now);
        await Assert.That(frontier.Discover(b, a, 0, Now)).IsEqualTo(DiscoveryVerdict.Added);
        await Assert.That(frontier.Discover(a, b, 1, Now)).IsEqualTo(DiscoveryVerdict.AlreadyKnown);

        await Assert.That(frontier.Records.Count).IsEqualTo(2);
    }

    [Test]
    public async Task AServerThatRefersToItselfIsCountedAndNotFollowed()
    {
        var frontier = Frontier();
        var a = Host("a.example.org 4000");
        frontier.AddSeed(a, Now);

        await Assert.That(frontier.Discover(a, a, 0, Now)).IsEqualTo(DiscoveryVerdict.SelfReferral);
        await Assert.That(frontier.Records.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ALongerCycleTerminatesBecauseIdentityIsNormalised()
    {
        // A → B → C → A, with each hop spelling the next host differently. Identity is over the
        // normalised host, so the loop closes on the third hop instead of running for ever.
        var frontier = Frontier();
        var a = Host("a.example.org 4000");
        var b = Host("b.example.org 4000");
        var c = Host("c.example.org 4000");

        frontier.AddSeed(a, Now);
        frontier.Discover(b, a, 0, Now);
        frontier.Discover(c, b, 1, Now);

        await Assert.That(frontier.Discover(Host("A.Example.ORG. 4000"), c, 2, Now))
            .IsEqualTo(DiscoveryVerdict.AlreadyKnown);
        await Assert.That(frontier.Records.Count).IsEqualTo(3);
    }

    [Test]
    public async Task AHostReachedAgainByAShorterPathKeepsTheShorterDepth()
    {
        // Depth is what the cap is measured against, so a host first met at the limit would otherwise
        // never have its own referrals followed even after a shorter route to it turned up.
        var frontier = Frontier();
        var seed = Host("seed.example.org 4000");
        var far = Host("far.example.org 4000");

        frontier.AddSeed(seed, Now);
        frontier.Discover(far, seed, 3, Now);
        await Assert.That(frontier.Records.Single(r => r.Host == far).Depth).IsEqualTo(4);

        frontier.Discover(far, seed, 0, Now);
        await Assert.That(frontier.Records.Single(r => r.Host == far).Depth).IsEqualTo(1);
    }

    [Test]
    public async Task AReferralBeyondTheDepthLimitIsRefused()
    {
        var frontier = Frontier(new CrawlOptions { MaxDepth = 2 });
        frontier.AddSeed(Host("seed.example.org 4000"), Now);

        var referrer = Host("seed.example.org 4000");
        await Assert.That(frontier.Discover(Host("a 4000"), referrer, 1, Now)).IsEqualTo(DiscoveryVerdict.Added);
        await Assert.That(frontier.Discover(Host("b 4000"), referrer, 2, Now)).IsEqualTo(DiscoveryVerdict.TooDeep);
    }

    [Test]
    public async Task AReferralIntoPrivateSpaceIsRefusedUnlessTheOperatorAsksForIt()
    {
        var referrer = Host("stranger.example.org 4000");

        var guarded = Frontier();
        guarded.AddSeed(referrer, Now);
        await Assert.That(guarded.Discover(Host("169.254.169.254 80"), referrer, 0, Now))
            .IsEqualTo(DiscoveryVerdict.NotRoutable);
        await Assert.That(guarded.Discover(Host("127.0.0.1 4201"), referrer, 0, Now))
            .IsEqualTo(DiscoveryVerdict.NotRoutable);

        var permissive = new CrawlFrontier(new CrawlOptions { FollowPrivateAddresses = true });
        permissive.AddSeed(referrer, Now);
        await Assert.That(permissive.Discover(Host("127.0.0.1 4201"), referrer, 0, Now))
            .IsEqualTo(DiscoveryVerdict.Added);
    }

    [Test]
    public async Task ASeedIntoPrivateSpaceIsAcceptedBecauseTheOperatorSaidSo()
    {
        // The operator pointing this at their own test server is not the same thing as a stranger's
        // referral aiming it at a network it could not otherwise reach.
        var frontier = Frontier();
        await Assert.That(frontier.AddSeed(Host("127.0.0.1 4201"), Now)).IsEqualTo(DiscoveryVerdict.Added);
    }

    [Test]
    public async Task ReferralsAreNotFollowedAtAllWhenTheRunSaysNotTo()
    {
        var frontier = Frontier(new CrawlOptions { FollowReferrals = false });
        var a = Host("a.example.org 4000");
        frontier.AddSeed(a, Now);

        await Assert.That(frontier.Discover(Host("b.example.org 4000"), a, 0, Now))
            .IsEqualTo(DiscoveryVerdict.ReferralsDisabled);
        await Assert.That(frontier.Records.Count).IsEqualTo(1);
    }
}
