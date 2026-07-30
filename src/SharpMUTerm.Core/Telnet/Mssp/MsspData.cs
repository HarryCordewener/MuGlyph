using System.Collections;
using System.Globalization;

namespace SharpMUTerm.Core.Telnet.Mssp;

/// <summary>Where a set of MSSP values came from, and therefore how much of it survived.</summary>
public enum MsspSource
{
    /// <summary>
    /// Parsed from the subnegotiation bytes by <see cref="MsspSubnegotiationParser"/>: every variable
    /// the server sent, every value of every array, in wire order.
    /// </summary>
    Wire,

    /// <summary>
    /// Projected from TelnetNegotiationCore's own <c>MSSPConfig</c>. Lossy — see
    /// <see cref="MsspData.FromInterpreterConfig"/> for exactly what the library discards. Only used
    /// when the wire bytes were unavailable, which in practice means MCCP compression was already
    /// running when the payload arrived.
    /// </summary>
    Interpreter,
}

/// <summary>
/// One server's MSSP report: every variable it sent, with every value, in the order it sent them.
/// <para>
/// The shape is a map from a canonical variable name (see <see cref="MsspVariables.Canonicalise"/>)
/// to an <em>ordered list</em> of values, and that is not incidental. MSSP has two ways to attach
/// several values to one variable — repeating the variable, and repeating <c>MSSP_VAL</c> under one
/// variable — and the specification gives both the same meaning: "multiple values should be ordered
/// from least to most relevant", with "the default value reported last". A model that kept one value
/// per variable would silently pick a server's <em>least</em> preferred port, and would lose
/// <c>REFERRAL</c> entirely, since a referral list is nothing but an array.
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

    private MsspData(Dictionary<string, IReadOnlyList<string>> values, List<string> order, MsspSource source)
    {
        _values = values;
        _order = order;
        Source = source;
    }

    /// <summary>An empty report — a server that negotiated MSSP and then said nothing.</summary>
    public static MsspData Empty { get; } = new([], [], MsspSource.Wire);

    /// <summary>How faithfully this data reflects what the server sent.</summary>
    public MsspSource Source { get; }

    /// <summary>Variable names in the order the server first mentioned them.</summary>
    public IEnumerable<string> Keys => _order;

    public IEnumerable<IReadOnlyList<string>> Values => _order.Select(name => _values[name]);

    public int Count => _order.Count;

    /// <summary>Every value of <paramref name="variable"/>, in wire order; empty when it was not sent.</summary>
    public IReadOnlyList<string> this[string variable] =>
        _values.TryGetValue(MsspVariables.Canonicalise(variable), out var values) ? values : [];

    /// <summary>The names in this report the specification defines, in wire order.</summary>
    public IReadOnlyList<string> OfficialNames => _order.Where(MsspVariables.IsOfficial).ToList();

    /// <summary>The names in this report the specification does not define, in wire order.</summary>
    public IReadOnlyList<string> UnofficialNames => _order.Where(n => !MsspVariables.IsOfficial(n)).ToList();

    public bool ContainsKey(string variable) => _values.ContainsKey(MsspVariables.Canonicalise(variable));

    public bool TryGetValue(string variable, out IReadOnlyList<string> values) =>
        _values.TryGetValue(MsspVariables.Canonicalise(variable), out values!);

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
    /// Rebuilds a report from a flat name → values map (a persisted record read back from disk).
    /// Names are canonicalised on the way in, so a store written by an older spelling still loads.
    /// </summary>
    public static MsspData FromValues(
        IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> values,
        MsspSource source = MsspSource.Wire)
    {
        var builder = new Builder(source);
        foreach (var (name, list) in values)
        {
            foreach (var value in list)
            {
                builder.Add(name, value);
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// Projects TelnetNegotiationCore's own <c>MSSPConfig</c> into this model — the degraded path,
    /// used only when the subnegotiation bytes were not observable.
    /// <para>
    /// <b>What the library loses, verified against 2.6.0.</b> Its MSSP reader accumulates every
    /// <c>MSSP_VAL</c> under one variable into a single byte buffer with no separator, so array
    /// notation is destroyed before any model sees it: <c>PORT 80 23 4201</c> arrives as the integer
    /// <c>80234201</c>, and <c>REFERRAL</c> — whose entire content is an array — is a concatenation
    /// that then fails to bind to the list-typed property and is dropped to null. Boolean variables
    /// (<c>ANSI</c>, <c>UTF-8</c>, <c>PAY TO PLAY</c> …) fail to bind from their string form and are
    /// dropped. Variables outside the library's model — including the official <c>CHARSET</c> — are
    /// dropped rather than collected into its <c>Extended</c> dictionary, which stays empty.
    /// </para>
    /// <para>
    /// This projection therefore recovers the scalars and nothing else, and marks itself
    /// <see cref="MsspSource.Interpreter"/> so a consumer can tell. It is a fallback, not a design:
    /// the fix belongs upstream, and <see cref="MsspSubnegotiationParser"/> is why the primary path
    /// does not need it.
    /// </para>
    /// </summary>
    public static MsspData FromInterpreterConfig(object? config)
    {
        var builder = new Builder(MsspSource.Interpreter);
        if (config is null)
        {
            return builder.Build();
        }

        foreach (var property in config.GetType().GetProperties())
        {
            if (property.GetIndexParameters().Length != 0 || !property.CanRead)
            {
                continue;
            }

            object? value;
            try
            {
                value = property.GetValue(config);
            }
            catch
            {
                continue;
            }

            if (value is null)
            {
                continue;
            }

            // The library tags each property with the MSSP name it came from; without that the model's
            // C# spelling (Minimum_Age, UTF_8, XTerm_256_Colors) would leak out as if it were the
            // protocol's, which is what this projection previously did.
            var wireName = property.GetCustomAttributesData()
                .FirstOrDefault(a => a.AttributeType.Name == "NameAttribute")
                ?.ConstructorArguments is [{ Value: string named }, ..]
                ? named
                : null;

            if (value is IDictionary extended)
            {
                foreach (DictionaryEntry entry in extended)
                {
                    if (entry.Key is string key && entry.Value is { } item)
                    {
                        builder.Add(key, item.ToString() ?? string.Empty);
                    }
                }

                continue;
            }

            if (wireName is null)
            {
                continue;
            }

            if (value is IEnumerable items and not string)
            {
                foreach (var item in items)
                {
                    if (item is not null)
                    {
                        builder.Add(wireName, item.ToString() ?? string.Empty);
                    }
                }

                continue;
            }

            builder.Add(wireName, value switch
            {
                bool flag => flag ? "1" : "0",
                _ => value.ToString() ?? string.Empty,
            });
        }

        return builder.Build();
    }

    /// <summary>Accumulates variables and values in wire order.</summary>
    public sealed class Builder(MsspSource source = MsspSource.Wire)
    {
        private readonly Dictionary<string, List<string>> _values = [];
        private readonly List<string> _order = [];

        /// <summary>
        /// Records one value of one variable. Repeating a variable appends to its list rather than
        /// replacing it, which is what makes the two ways MSSP spells an array — repeated variables and
        /// repeated values — end up in one place and keep their order.
        /// </summary>
        public Builder Add(string variable, string value)
        {
            List(variable)?.Add(value);
            return this;
        }

        /// <summary>
        /// Records that a variable was sent without recording a value for it. A variable with no
        /// <c>MSSP_VAL</c> at all is malformed, but "the server mentioned this and said nothing" is a
        /// different fact from "the server never mentioned it", and inventing an empty value to carry
        /// the first would erase the difference.
        /// </summary>
        public Builder Declare(string variable)
        {
            List(variable);
            return this;
        }

        private List<string>? List(string variable)
        {
            var name = MsspVariables.Canonicalise(variable);
            if (name.Length == 0)
            {
                return null;
            }

            if (!_values.TryGetValue(name, out var list))
            {
                list = [];
                _values[name] = list;
                _order.Add(name);
            }

            return list;
        }

        public bool IsEmpty => _order.Count == 0;

        public MsspData Build() =>
            new(_values.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value), [.. _order], source);
    }
}
