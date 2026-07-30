using System.Text;
using SharpMUTerm.Core.Telnet.Mssp;

namespace SharpMUTerm.Crawler.Tests.Support;

/// <summary>
/// Builds MSSP subnegotiations byte by byte, exactly as the specification spells them, so a test
/// asserts against the wire rather than against a model's idea of it.
/// </summary>
internal static class MsspWire
{
    public const byte Iac = 255;
    public const byte Se = 240;
    public const byte Sb = 250;
    public const byte Will = 251;
    public const byte Wont = 252;
    public const byte Do = 253;
    public const byte Dont = 254;
    public const byte Mssp = 70;
    public const byte Gmcp = 201;
    public const byte Var = 1;
    public const byte Val = 2;

    /// <summary>
    /// <c>IAC SB MSSP MSSP_VAR "x" MSSP_VAL "y" … IAC SE</c>. Each entry is one variable and all of its
    /// values, so array notation — several <c>MSSP_VAL</c> under one <c>MSSP_VAR</c> — is expressible,
    /// which is the whole point.
    /// </summary>
    public static byte[] Subnegotiation(params (string Variable, string[] Values)[] entries)
    {
        var bytes = new List<byte> { Iac, Sb, Mssp };
        foreach (var (variable, values) in entries)
        {
            bytes.Add(Var);
            bytes.AddRange(Encoding.UTF8.GetBytes(variable));
            foreach (var value in values)
            {
                bytes.Add(Val);
                bytes.AddRange(Encoding.UTF8.GetBytes(value));
            }
        }

        bytes.AddRange([Iac, Se]);
        return [.. bytes];
    }

    /// <summary>
    /// The same entries as an <see cref="MsspData"/>, without a socket. For the tests whose subject is
    /// what the crawler <em>does</em> with a report — scheduling, referral following, persistence —
    /// rather than how the report was read. Reading it is the telnet layer's job and is pinned, once,
    /// by <c>MsspParsingTests</c> driving a real session.
    /// </summary>
    public static MsspData Report(params (string Variable, string[] Values)[] entries) =>
        MsspData.From(entries.Select(entry =>
            new KeyValuePair<string, IReadOnlyList<string>>(entry.Variable, entry.Values)));

    /// <summary>A server offering MSSP, then answering the client's <c>DO</c> with a report.</summary>
    public static byte[] Offer() => [Iac, Will, Mssp];

    /// <summary>The full sequence for a representative real server, used by several tests.</summary>
    public static (string Variable, string[] Values)[] RepresentativeReport(params string[] referrals) =>
    [
        ("NAME", ["Corvid Nest"]),
        ("PLAYERS", ["17"]),
        ("UPTIME", ["1735689600"]),

        // Array notation, most important last.
        ("PORT", ["80", "23", "4201"]),
        ("HOSTNAME", ["corvid.example.org"]),
        ("CODEBASE", ["PennMUSH", "SharpMUSH 1.0"]),
        ("CONTACT", ["admin@corvid.example.org"]),
        ("CRAWL DELAY", ["5"]),
        ("WEBSITE", ["https://corvid.example.org/"]),
        ("FAMILY", ["TinyMUSH"]),
        ("GENRE", ["Fantasy"]),
        ("STATUS", ["Live"]),

        // The underscore spelling the specification says clients and crawlers may substitute.
        ("MINIMUM_AGE", ["13"]),

        // Booleans, in the 1/0 spelling the specification uses.
        ("ANSI", ["1"]),
        ("UTF-8", ["1"]),
        ("PAY TO PLAY", ["0"]),

        // An official variable with no strongly typed property on the library's own model.
        ("CHARSET", ["ASCII", "UTF-8"]),

        // Unofficial but widely deployed.
        ("PUEBLO", ["1"]),
        ("MSP", ["0"]),

        // Wholly unknown — invented by some codebase. Must survive.
        ("CORVID SPECIFIC", ["nevermore"]),
        ("VANITY", ["least", "most"]),

        ("REFERRAL", referrals),
    ];
}
