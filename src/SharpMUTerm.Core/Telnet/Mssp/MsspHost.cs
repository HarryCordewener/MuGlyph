using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace SharpMUTerm.Core.Telnet.Mssp;

/// <summary>
/// What kind of address a host names, as far as can be told without resolving it. The crawler uses
/// this to refuse referrals that point inside a network rather than at a public game server.
/// </summary>
public enum MsspHostScope
{
    /// <summary>A name, not a literal — nothing can be told about it until DNS answers.</summary>
    Unresolved,

    /// <summary>A globally routable IP literal.</summary>
    Global,

    /// <summary>127.0.0.0/8, ::1, or the unspecified address.</summary>
    Loopback,

    /// <summary>RFC 1918 / RFC 4193 space, or a carrier-grade NAT range.</summary>
    Private,

    /// <summary>169.254.0.0/16 or fe80::/10 — including the cloud metadata address.</summary>
    LinkLocal,

    /// <summary>A multicast or broadcast group; never a game server.</summary>
    Multicast,
}

/// <summary>
/// A host and port as MSSP names one: the unit a <c>REFERRAL</c> value carries, and the identity a
/// crawler deduplicates on.
/// <para>
/// The specification is explicit about the wire format: a referral uses "the host port format and
/// array notation … Make sure to separate the host and port with a space rather than <c>:</c>
/// because IPv6 addresses contain colons." <see cref="TryParse"/> implements exactly that, and
/// tolerates <c>host:port</c> only where it cannot be an IPv6 literal (see its remarks).
/// </para>
/// <para>
/// Equality is over the <em>normalised</em> host and the port, which is what makes cycle detection
/// work: <c>MUD.Example.ORG.</c>, <c>mud.example.org</c> and <c>MUD.EXAMPLE.ORG</c> are one host, and
/// <c>2001:0DB8:0000::0001</c> and <c>2001:db8::1</c> are one address. Without that a crawl walks the
/// same server once per spelling its peers happen to use.
/// </para>
/// </summary>
public sealed record MsspHost
{
    private MsspHost(string host, int port, MsspHostScope scope)
    {
        Host = host;
        Port = port;
        Scope = scope;
    }

    /// <summary>The normalised host: lower-cased, trailing dot removed, IP literals in canonical form.</summary>
    public string Host { get; }

    /// <summary>The TCP port, 1–65535.</summary>
    public int Port { get; }

    /// <summary>What kind of address <see cref="Host"/> is, as far as a literal reveals.</summary>
    public MsspHostScope Scope { get; }

    /// <summary>True when this is an IPv6 literal, which display has to bracket.</summary>
    public bool IsIpV6 =>
        IPAddress.TryParse(Host, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6;

    /// <summary>
    /// True when this host is worth a crawler's time: a name, or a globally routable literal.
    /// A referral into loopback, RFC 1918 space, or link-local (which includes the cloud metadata
    /// address, <c>169.254.169.254</c>) is either a misconfiguration or an attempt to make someone
    /// else's crawler probe their own network, and is neither useful nor polite to follow.
    /// </summary>
    public bool IsCrawlable => Scope is MsspHostScope.Unresolved or MsspHostScope.Global;

    /// <summary>
    /// Builds a host from an already-separated host and port, normalising the host. Returns null when
    /// the host is empty or malformed, or the port is outside 1–65535.
    /// </summary>
    public static MsspHost? Create(string? host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
        {
            return null;
        }

        var trimmed = host.Trim();

        // A bracketed IPv6 literal, [2001:db8::1], as URLs spell it. The brackets are punctuation for
        // the colon problem this format does not have, so they are stripped rather than kept.
        if (trimmed.Length > 2 && trimmed[0] == '[' && trimmed[^1] == ']')
        {
            trimmed = trimmed[1..^1].Trim();
        }

        if (trimmed.Length == 0)
        {
            return null;
        }

        if (IPAddress.TryParse(trimmed, out var address))
        {
            // ToString() is the canonical form: it compresses IPv6 zero runs and strips leading zeroes,
            // so two spellings of one address become one key.
            return new MsspHost(address.ToString().ToLowerInvariant(), port, Classify(address));
        }

        // A DNS name. The root label's trailing dot is legal and means the same name, so it goes.
        var name = trimmed.TrimEnd('.').ToLowerInvariant();
        return name.Length != 0 && IsPlausibleName(name)
            ? new MsspHost(name, port, MsspHostScope.Unresolved)
            : null;
    }

    /// <summary>
    /// Parses one <c>REFERRAL</c> array element.
    /// <para>
    /// The specification's format is <c>host port</c>, whitespace-separated, precisely so an IPv6
    /// literal's colons are unambiguous. That is the only form parsed without reservation.
    /// </para>
    /// <para>
    /// <c>host:port</c> is also accepted, but <b>only when the string contains exactly one colon</b> —
    /// which is to say only when it cannot be an IPv6 literal. Real servers do emit the colon form
    /// despite the specification, and refusing it loses referrals for no gain; but a string with two
    /// or more colons is far more likely to be a bare IPv6 address than a host:port pair, and guessing
    /// wrong there would silently rewrite an address into a different one. Those are rejected.
    /// </para>
    /// </summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out MsspHost? host)
    {
        host = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();

        // The specified form: host, whitespace, port. Split on the *last* run of whitespace so a
        // (malformed) host containing a space still yields the port.
        var separator = text.LastIndexOfAny([' ', '\t']);
        if (separator > 0)
        {
            var portText = text[(separator + 1)..];
            if (int.TryParse(portText, out var spacedPort))
            {
                host = Create(text[..separator], spacedPort);
                return host is not null;
            }
        }

        // The tolerated form: host:port, only where no IPv6 literal could be meant.
        var colon = text.IndexOf(':');
        if (colon > 0 && text.IndexOf(':', colon + 1) < 0 && int.TryParse(text[(colon + 1)..], out var colonPort))
        {
            host = Create(text[..colon], colonPort);
            return host is not null;
        }

        return false;
    }

    /// <summary>The wire form MSSP uses: host, a space, port.</summary>
    public string ToReferralString() => $"{Host} {Port}";

    /// <summary>The display form, bracketing IPv6 literals the way a URL would.</summary>
    public override string ToString() => IsIpV6 ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    private static MsspHostScope Classify(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return MsspHostScope.Loopback;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal)
            {
                return MsspHostScope.LinkLocal;
            }

            if (address.IsIPv6Multicast)
            {
                return MsspHostScope.Multicast;
            }

            // Unique local addresses, fc00::/7 — the IPv6 analogue of RFC 1918.
            var v6 = address.GetAddressBytes();
            if ((v6[0] & 0xFE) == 0xFC)
            {
                return MsspHostScope.Private;
            }

            // An IPv4-mapped address wears IPv6 clothes but is an IPv4 address; classify what it is.
            return address.IsIPv4MappedToIPv6
                ? Classify(address.MapToIPv4())
                : MsspHostScope.Global;
        }

        var b = address.GetAddressBytes();
        return b[0] switch
        {
            10 => MsspHostScope.Private,
            127 => MsspHostScope.Loopback,
            169 when b[1] == 254 => MsspHostScope.LinkLocal,
            172 when b[1] is >= 16 and <= 31 => MsspHostScope.Private,
            192 when b[1] == 168 => MsspHostScope.Private,
            100 when b[1] is >= 64 and <= 127 => MsspHostScope.Private, // RFC 6598 carrier-grade NAT
            >= 224 => MsspHostScope.Multicast,
            _ => MsspHostScope.Global,
        };
    }

    /// <summary>
    /// A cheap sanity filter on a DNS name, so a value that is plainly not a host — a sentence, a
    /// URL, a control character smuggled through — never reaches the resolver. It is deliberately
    /// permissive about which characters a label may hold (underscores and non-ASCII both occur in
    /// the wild) and strict only about what a host name can never contain.
    /// </summary>
    private static bool IsPlausibleName(string name)
    {
        if (name.Length > 253 || name.StartsWith('.') || name.Contains(".."))
        {
            return false;
        }

        foreach (var ch in name)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch) || ch is '/' or '\\' or '@' or ':' or '?' or '#')
            {
                return false;
            }
        }

        return true;
    }
}
