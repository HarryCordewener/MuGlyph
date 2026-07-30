using System.Collections;
using System.Globalization;
using TelnetNegotiationCore.Models;

namespace SharpMUTerm.Core.Telnet.Mssp;

/// <summary>
/// One server's MSSP report, in the shape this application wants it: every variable it sent, with
/// every value, in the order it sent them, plus the domain readings a client and a crawler ask for.
/// <para>
/// <b>This projects; it does not parse.</b> The bytes are read by TelnetNegotiationCore, which hands
/// back an ordered name → value-list map (<c>MSSPConfig.Variables</c>); everything here is built from
/// that. What this type adds over the library's own collection is the part that is ours rather than
/// the protocol's: <c>REFERRAL</c> read as <see cref="MsspHost"/>s a crawler can follow and
/// deduplicate, <c>CRAWL DELAY</c> read as the specification's "no preference" rather than a negative
/// interval, ports validated as ports, and an immutable snapshot a report can be written from.
/// </para>
/// <para>
/// The shape is a map from a canonical variable name to an <em>ordered list</em> of values, and that
/// is not incidental. MSSP has two ways to attach several values to one variable — repeating the
/// variable, and repeating <c>MSSP_VAL</c> under one variable — and the specification gives both the
/// same meaning: "multiple values should be ordered from least to most relevant", with "the default
/// value reported last". A model that kept one value per variable would silently pick a server's
/// <em>least</em> preferred port, and would lose <c>REFERRAL</c> entirely, since a referral list is
/// nothing but an array.
/// </para>
/// <para>
/// Nothing is discarded on the way in. Variables the specification does not define are kept beside
/// the ones it does (<see cref="UnofficialNames"/>), because a crawler's job is to record what a
/// server said rather than what a model expected it to say, and because MSSP's unofficial half is
/// where several widely-deployed variables live.
/// </para>
/// </summary>
public sealed class MsspData : IReadOnlyDictionary<string, IReadOnlyList<string>>
{
    private readonly Dictionary<string, IReadOnlyList<string>> _values;
    private readonly List<string> _order;

    private MsspData(Dictionary<string, IReadOnlyList<string>> values, List<string> order)
    {
        _values = values;
        _order = order;
    }

    /// <summary>An empty report — a server that negotiated MSSP and then said nothing.</summary>
    public static MsspData Empty { get; } = new([], []);

    /// <summary>Variable names in the order the server first mentioned them.</summary>
    public IEnumerable<string> Keys => _order;

    public IEnumerable<IReadOnlyList<string>> Values => _order.Select(name => _values[name]);

    public int Count => _order.Count;

    /// <summary>Every value of <paramref name="variable"/>, in wire order; empty when it was not sent.</summary>
    public IReadOnlyList<string> this[string variable] =>
        _values.TryGetValue(MSSPVariables.Canonicalize(variable), out var values) ? values : [];

    /// <summary>The names in this report the specification defines, in wire order.</summary>
    public IReadOnlyList<string> OfficialNames => _order.Where(MSSPVariables.IsOfficial).ToList();

    /// <summary>The names in this report the specification does not define, in wire order.</summary>
    public IReadOnlyList<string> UnofficialNames => _order.Where(n => !MSSPVariables.IsOfficial(n)).ToList();

    public bool ContainsKey(string variable) => _values.ContainsKey(MSSPVariables.Canonicalize(variable));

    public bool TryGetValue(string variable, out IReadOnlyList<string> values) =>
        _values.TryGetValue(MSSPVariables.Canonicalize(variable), out values!);

    /// <summary>
    /// The <em>default</em> value of <paramref name="variable"/> — the last one sent, per the
    /// specification — or null when the server did not send it.
    /// </summary>
    public string? Default(string variable)
    {
        var values = this[variable];
        return values.Count == 0 ? null : values[^1];
    }

    // ---- The variables anything reading MSSP actually asks for ----

    /// <summary>The MUD's name, or null.</summary>
    public string? Name => Default(MsspVariables.Name);

    /// <summary>Players currently logged in, or null when unreported or unparseable.</summary>
    public int? Players => Integer(MsspVariables.Players);

    /// <summary>The Unix timestamp the server booted at, or null.</summary>
    public DateTimeOffset? Uptime =>
        long.TryParse(Default(MsspVariables.Uptime), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)
        && unix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : null;

    /// <summary>The server's preferred port — the last one listed, which the specification calls the most important.</summary>
    public int? Port => Integer(MsspVariables.Port);

    /// <summary>Every port the server listed, least to most important.</summary>
    public IReadOnlyList<int> Ports => this[MsspVariables.Port]
        .Select(v => int.TryParse(v, out var p) ? p : -1)
        .Where(p => p is > 0 and <= 65535)
        .ToList();

    /// <summary>The hostname the server says it is reachable at, or null.</summary>
    public string? Hostname => Default(MsspVariables.Hostname);

    /// <summary>Contact e-mail, or null.</summary>
    public string? Contact => Default(MsspVariables.Contact);

    /// <summary>Website URL, or null.</summary>
    public string? Website => Default(MsspVariables.Website);

    /// <summary>The current codebase — the last listed, per the specification.</summary>
    public string? Codebase => Default(MsspVariables.Codebase);

    /// <summary>The family — the last listed, which the specification says is the most distant ancestor.</summary>
    public string? Family => Default(MsspVariables.Family);

    /// <summary>
    /// How long the server asks a crawler to leave between visits, or null when it did not say or
    /// asked for the crawler's own default.
    /// <para>
    /// The specification defines <c>CRAWL DELAY</c> as a "preferred minimum number of hours between
    /// crawls" and gives <c>-1</c> the meaning "use the crawler's default". A negative value therefore
    /// resolves to null here rather than to a negative interval — the distinction matters, because a
    /// caller combining this with its own default must be able to tell "no preference" from "zero".
    /// </para>
    /// </summary>
    public TimeSpan? CrawlDelay =>
        int.TryParse(Default(MsspVariables.CrawlDelay), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
        && hours >= 0
            ? TimeSpan.FromHours(hours)
            : null;

    /// <summary>
    /// The peers this server points a crawler at: every parseable <c>REFERRAL</c> value, deduplicated,
    /// in the order given.
    /// <para>
    /// Values that do not parse are dropped silently rather than surfaced as errors. A referral list is
    /// hand-maintained configuration on somebody else's server; one stale line in it is not a fault in
    /// the report, and the raw strings remain available through <c>this["REFERRAL"]</c> for anything
    /// that wants to audit them.
    /// </para>
    /// </summary>
    public IReadOnlyList<MsspHost> Referrals
    {
        get
        {
            var seen = new HashSet<MsspHost>();
            var result = new List<MsspHost>();
            foreach (var value in this[MsspVariables.Referral])
            {
                if (MsspHost.TryParse(value, out var host) && seen.Add(host))
                {
                    result.Add(host);
                }
            }

            return result;
        }
    }

    /// <summary>An MSSP boolean (<c>1</c> or <c>0</c>), or null when unreported or unparseable.</summary>
    public bool? Flag(string variable) => Default(variable) switch
    {
        "1" => true,
        "0" => false,
        _ => null,
    };

    /// <summary>
    /// An MSSP integer, or null when unreported or unparseable. <c>-1</c> is the specification's
    /// "data not available" marker for the World counts and resolves to null, not to minus one.
    /// <para>
    /// This is deliberately narrower than the library's own <c>MSSPVariableCollection.Integer</c>,
    /// which returns <c>-1</c> as-is on the grounds that a caller may want to tell "the server said it
    /// cannot count its rooms" from "the server never mentioned rooms". Everything reading this type
    /// wants a count it can print or compare, and the raw string is still one indexer away.
    /// </para>
    /// </summary>
    public int? Integer(string variable) =>
        int.TryParse(Default(variable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && value >= 0
            ? value
            : null;

    public IEnumerator<KeyValuePair<string, IReadOnlyList<string>>> GetEnumerator() =>
        _order.Select(name => new KeyValuePair<string, IReadOnlyList<string>>(name, _values[name])).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Projects a name → values map into this model, keeping every value of every variable in the
    /// order given. The library's own <c>MSSPConfig.Variables</c> is exactly such a map, which is the
    /// path a live session takes; a flat dictionary read back from a file is the other.
    /// <para>
    /// Names are canonicalised on the way in — by the library's <see cref="MSSPVariables.Canonicalize"/>,
    /// so there is one vocabulary in the solution rather than two — which means a source that spells
    /// <c>MINIMUM_AGE</c> and <c>MINIMUM AGE</c> separately still yields one variable. A name that
    /// canonicalises to nothing is dropped; a variable with no values is kept, because "the server
    /// mentioned this and said nothing" is a different fact from "the server never mentioned it".
    /// </para>
    /// </summary>
    public static MsspData From(IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var (variable, list) in variables)
        {
            var name = MSSPVariables.Canonicalize(variable);
            if (name.Length == 0)
            {
                continue;
            }

            if (!values.TryGetValue(name, out var accumulated))
            {
                accumulated = [];
                values[name] = accumulated;
                order.Add(name);
            }

            accumulated.AddRange(list);
        }

        return new MsspData(values.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value), order);
    }
}
