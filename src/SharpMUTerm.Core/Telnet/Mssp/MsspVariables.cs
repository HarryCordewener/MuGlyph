namespace SharpMUTerm.Core.Telnet.Mssp;

/// <summary>
/// The canonical spellings of the MSSP variables this application reads by name — the ones
/// <see cref="MsspData"/>'s domain accessors are built on, and the ones an MSSP screen would label.
/// <para>
/// Names only. The vocabulary <em>rules</em> — which names are official, and the folding that makes
/// <c>MINIMUM_AGE</c> and <c>MINIMUM AGE</c> one variable — belong to
/// <c>TelnetNegotiationCore.Models.MSSPVariables</c>, which derives them from the same model that
/// reads the wire. Two copies of a vocabulary drift; this one exists so a domain accessor can say
/// what it is reading rather than repeating a string literal.
/// </para>
/// <para>
/// Canonical form is the <em>spaced, upper-case</em> spelling — what the specification's own tables
/// print — because that is what a human reading a report expects to see and what a server operator
/// would recognise from their own configuration. The underscore form is an encoding convenience, not
/// the name; <c>UTF-8</c> keeps its hyphen, because no underscore is involved.
/// See <see href="https://mudhalla.net/tintin/protocols/mssp/">the specification</see>.
/// </para>
/// </summary>
public static class MsspVariables
{
    // ---- Required ----
    public const string Name = "NAME";
    public const string Players = "PLAYERS";
    public const string Uptime = "UPTIME";

    // ---- Generic ----
    public const string Charset = "CHARSET";
    public const string Codebase = "CODEBASE";
    public const string Contact = "CONTACT";
    public const string CrawlDelay = "CRAWL DELAY";
    public const string Hostname = "HOSTNAME";
    public const string MinimumAge = "MINIMUM AGE";
    public const string Port = "PORT";
    public const string Referral = "REFERRAL";
    public const string Ssl = "SSL";
    public const string Website = "WEBSITE";

    // ---- Categorisation ----
    public const string Family = "FAMILY";
    public const string Genre = "GENRE";
    public const string Status = "STATUS";
}
