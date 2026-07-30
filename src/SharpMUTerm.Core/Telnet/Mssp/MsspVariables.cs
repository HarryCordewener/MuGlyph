namespace SharpMUTerm.Core.Telnet.Mssp;

/// <summary>
/// The MSSP variable vocabulary: the names the specification defines, and the one canonical
/// spelling this codebase stores them under.
/// <para>
/// The specification (<c>https://mudhalla.net/tintin/protocols/mssp/</c>) says variable names
/// "should exist of upper case letters and may contain spaces", and that "as many programming
/// languages have difficulties with variable names which contain spaces clients and crawlers can
/// substitute spaces with underscores as the recommended solution". So a server may legitimately
/// send <c>MINIMUM AGE</c> or <c>MINIMUM_AGE</c> and mean one variable, and a case-insensitive
/// dictionary alone would treat them as two. <see cref="Canonicalise"/> is the single place that
/// decides they are the same thing; everything downstream keys on its output.
/// </para>
/// <para>
/// Canonical form is the <em>spaced, upper-case</em> spelling — what the specification's own tables
/// print — because that is what a human reading a report expects to see and what a server operator
/// would recognise from their own configuration. The underscore form is an encoding convenience,
/// not the name.
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
    public const string Created = "CREATED";
    public const string Discord = "DISCORD";
    public const string Hostname = "HOSTNAME";
    public const string Icon = "ICON";
    public const string Ip = "IP";
    public const string IpV6 = "IPV6";
    public const string Language = "LANGUAGE";
    public const string Location = "LOCATION";
    public const string MinimumAge = "MINIMUM AGE";
    public const string Port = "PORT";
    public const string Referral = "REFERRAL";
    public const string Ssl = "SSL";
    public const string Website = "WEBSITE";

    // ---- Categorisation ----
    public const string Family = "FAMILY";
    public const string Genre = "GENRE";
    public const string Gameplay = "GAMEPLAY";
    public const string Status = "STATUS";
    public const string GameSystem = "GAMESYSTEM";
    public const string Intermud = "INTERMUD";
    public const string Subgenre = "SUBGENRE";

    // ---- World ----
    public const string Areas = "AREAS";
    public const string Helpfiles = "HELPFILES";
    public const string Mobiles = "MOBILES";
    public const string Objects = "OBJECTS";
    public const string Rooms = "ROOMS";
    public const string Classes = "CLASSES";
    public const string Levels = "LEVELS";
    public const string Races = "RACES";
    public const string Skills = "SKILLS";

    // ---- Protocols (all 1/0) ----
    public const string Ansi = "ANSI";
    public const string Utf8 = "UTF-8";
    public const string Vt100 = "VT100";
    public const string Xterm256Colors = "XTERM 256 COLORS";
    public const string XtermTrueColors = "XTERM TRUE COLORS";

    // ---- Commercial / hiring (all 1/0) ----
    public const string PayToPlay = "PAY TO PLAY";
    public const string PayForPerks = "PAY FOR PERKS";
    public const string HiringBuilders = "HIRING BUILDERS";
    public const string HiringCoders = "HIRING CODERS";

    /// <summary>
    /// Every variable the specification's official tables list, in the order they appear there.
    /// Anything outside this set is "unofficial" — either one of the widely-deployed extras below,
    /// or something a codebase invented. Both are kept; neither is discarded.
    /// </summary>
    public static readonly IReadOnlyList<string> Official =
    [
        Name, Players, Uptime,
        Charset, Codebase, Contact, CrawlDelay, Created, Discord, Hostname, Icon, Ip, IpV6,
        Language, Location, MinimumAge, Port, Referral, Ssl, Website,
        Family, Genre, Gameplay, Status, GameSystem, Intermud, Subgenre,
        Areas, Helpfiles, Mobiles, Objects, Rooms, Classes, Levels, Races, Skills,
        Ansi, Utf8, Vt100, Xterm256Colors, XtermTrueColors,
        PayToPlay, PayForPerks, HiringBuilders, HiringCoders,
    ];

    /// <summary>
    /// Variables that are not in the specification's tables but are sent by enough real servers to be
    /// worth naming: the ones codebases and earlier drafts of MSSP shipped. They are recognised only
    /// so a report can group them sensibly — an unrecognised variable is kept exactly as faithfully.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownUnofficial =
    [
        "PUEBLO", "MSP", "MCCP", "MCP", "MXP", "GMCP", "MSDP", "SSH", "ATCP", "ZMP",
        "MULTICLASSING", "NEWBIE FRIENDLY", "PLAYER KILLING", "ROLEPLAYING", "TRAINING SYSTEM",
        "WORLD ORIGINALITY", "EQUIPMENT SYSTEM", "MULTIPLAYING", "PLAYERS ONLINE", "DBSIZE",
        "EXITS", "EXTRA DESCRIPTIONS", "RESETS", "SHOPS", "SOCIALS", "VOLUME",
    ];

    private static readonly HashSet<string> OfficialSet = new(Official, StringComparer.Ordinal);

    private static readonly HashSet<string> KnownUnofficialSet = new(KnownUnofficial, StringComparer.Ordinal);

    /// <summary>
    /// The one spelling a variable is stored under: trimmed, upper-cased, underscores folded to
    /// spaces, and runs of whitespace collapsed to one space.
    /// <para>
    /// Underscore folding is what makes <c>MINIMUM_AGE</c> and <c>MINIMUM AGE</c> one variable, per the
    /// specification's own note that the substitution is a recommended encoding of the same name. It is
    /// deliberately unconditional rather than restricted to names we recognise: a server that spells an
    /// unofficial variable both ways in one payload should still get one entry, and we cannot know
    /// which of its own names it considers canonical.
    /// </para>
    /// <para>
    /// <c>UTF-8</c> keeps its hyphen — the specification spells it that way and no underscore is
    /// involved, so nothing here touches it.
    /// </para>
    /// </summary>
    public static string Canonicalise(string variable)
    {
        ArgumentNullException.ThrowIfNull(variable);

        var folded = new System.Text.StringBuilder(variable.Length);
        var pendingSpace = false;
        foreach (var ch in variable)
        {
            var c = ch == '_' ? ' ' : ch;
            if (char.IsWhiteSpace(c))
            {
                // Leading whitespace is dropped; interior runs become one space, decided when the
                // next real character arrives so a trailing run costs nothing.
                pendingSpace = folded.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                folded.Append(' ');
                pendingSpace = false;
            }

            folded.Append(char.ToUpperInvariant(c));
        }

        return folded.ToString();
    }

    /// <summary>True when <paramref name="variable"/> is one of the specification's official variables.</summary>
    public static bool IsOfficial(string variable) => OfficialSet.Contains(Canonicalise(variable));

    /// <summary>
    /// True when <paramref name="variable"/> is unofficial but recognised — a variable real servers
    /// send that the specification does not define.
    /// </summary>
    public static bool IsKnownUnofficial(string variable) => KnownUnofficialSet.Contains(Canonicalise(variable));
}
