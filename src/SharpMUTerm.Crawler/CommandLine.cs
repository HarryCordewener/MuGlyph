using System.Globalization;
using System.Text.Json;
using SharpMUTerm.Core.Telnet.Mssp;

namespace SharpMUTerm.Crawler;

/// <summary>How a run was configured, or why it could not be.</summary>
public sealed record CommandLineResult
{
    public CrawlOptions? Options { get; init; }

    /// <summary>Lines to print before starting: rejected seeds, notes about the configuration.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public string? Error { get; init; }

    public bool WantsHelp { get; init; }
}

/// <summary>
/// Turns arguments into a <see cref="CrawlOptions"/>.
/// <para>
/// A JSON file supplies the defaults, flags override it, and flags win — the usual order, so a saved
/// configuration can be tried with one setting changed without editing it. The file is only ever the
/// one named by <c>--config</c>; nothing here looks for a configuration anywhere on its own.
/// </para>
/// </summary>
public static class CommandLine
{
    public const string Usage = """
        sharpmuterm-mssp-crawler — reads MSSP from MU* servers and follows REFERRAL to find more.

        It connects, negotiates telnet, takes the server's MSSP report and disconnects.
        It never logs in and never sends a command.
        It identifies itself over TTYPE as SHARPMUTERM-MSSPCRAWLER.

        USAGE
          sharpmuterm-mssp-crawler --seeds <file> [options]
          sharpmuterm-mssp-crawler --seed <host> <port> [options]

        SEEDS (at least one source is required on a first run)
          --seeds <file>              One host per line: "host port" (or host:port). # comments.
          --seed <host:port>          A single host; may be repeated.
          --seed-from-worlds <file>   Take host and port from a SharpMUTerm config file you name.
                                      Nothing else is read from it, ever — not characters, not
                                      logins, and never secrets.json. There is no default path:
                                      you must name the file.

        OUTPUT
          --out <dir>                 Where state, observations and the report go. Default: mssp-crawl
                                      Written: state.json, observations.jsonl, report.md

        POLITENESS
          --concurrency <n>           Connections at once. Default 4.
          --global-interval <sec>     Minimum gap between any two connections. Default 1.
          --per-host-interval <sec>   Minimum gap between two connections to one host. Default 300.
          --revisit <hours>           How long before a server that answered is asked again.
                                      Default 24. A server's own CRAWL DELAY is honoured when it
                                      asks for longer.
          --connect-timeout <sec>     Default 15.
          --mssp-timeout <sec>        How long to wait for MSSP once connected. Default 20.

        LIMITS (a run cannot exceed these; they are why it can be left alone)
          --max-hosts <n>             Total hosts contacted in one run. Default 500.
          --max-duration <min>        Wall-clock limit on the run. Default 60.
          --max-depth <n>             Referral hops from a seed. Default 4.

        BEHAVIOUR
          --no-referrals              Check the hosts already known; discover nothing new.
          --allow-private             Follow referrals to private/loopback addresses. Off by default.
          --dry-run                   Select hosts, contact nobody, write the report.
          --config <file>             JSON defaults; command-line flags override them.
          --quiet                     Only warnings and errors.
          -h, --help                  This text.
        """;

    public static CommandLineResult Parse(IReadOnlyList<string> args)
    {
        var notes = new List<string>();
        var seeds = new List<MsspHost>();
        var options = new CrawlOptions();

        // Applied first so flags can override it, whatever order they appear in.
        var configIndex = IndexOfFlag(args, "--config");
        if (configIndex >= 0)
        {
            if (configIndex + 1 >= args.Count)
            {
                return new CommandLineResult { Error = "--config needs a file." };
            }

            try
            {
                options = ApplyFile(options, args[configIndex + 1]);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                return new CommandLineResult { Error = $"--config: {ex.Message}" };
            }
        }

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            string? Next(string flag)
            {
                if (i + 1 < args.Count)
                {
                    return args[++i];
                }

                notes.Add($"{flag} needs a value.");
                return null;
            }

            switch (arg)
            {
                case "-h" or "--help":
                    return new CommandLineResult { WantsHelp = true };

                case "--config":
                    i++; // already applied
                    break;

                case "--seeds":
                    if (Next(arg) is { } seedFile)
                    {
                        try
                        {
                            var list = Seeds.FromFile(seedFile);
                            seeds.AddRange(list.Hosts);
                            notes.AddRange(list.Rejected.Select(line => $"seed file: could not read \"{line}\""));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            return new CommandLineResult { Error = $"--seeds: {ex.Message}" };
                        }
                    }

                    break;

                case "--seed":
                    if (Next(arg) is { } single)
                    {
                        if (MsspHost.TryParse(single, out var host))
                        {
                            seeds.Add(host);
                        }
                        else
                        {
                            return new CommandLineResult { Error = $"--seed: \"{single}\" is not host:port or \"host port\"." };
                        }
                    }

                    break;

                case "--seed-from-worlds":
                    if (Next(arg) is { } worlds)
                    {
                        try
                        {
                            var list = Seeds.FromWorldsFile(worlds);
                            seeds.AddRange(list.Hosts);
                            notes.Add($"took {list.Hosts.Count} host(s) from {worlds} — host and port only.");
                            notes.AddRange(list.Rejected.Select(line => $"worlds file: skipped {line}"));
                        }
                        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                        {
                            return new CommandLineResult { Error = $"--seed-from-worlds: {ex.Message}" };
                        }
                    }

                    break;

                case "--out":
                    if (Next(arg) is { } directory)
                    {
                        options = options with { OutputDirectory = directory };
                    }

                    break;

                case "--concurrency":
                    if (Integer(Next(arg), arg, notes) is { } concurrency)
                    {
                        options = options with { MaxConcurrency = concurrency };
                    }

                    break;

                case "--global-interval":
                    if (Seconds(Next(arg), arg, notes) is { } global)
                    {
                        options = options with { GlobalInterval = global };
                    }

                    break;

                case "--per-host-interval":
                    if (Seconds(Next(arg), arg, notes) is { } perHost)
                    {
                        options = options with { PerHostInterval = perHost };
                    }

                    break;

                case "--revisit":
                    if (Hours(Next(arg), arg, notes) is { } revisit)
                    {
                        options = options with { RevisitInterval = revisit };
                    }

                    break;

                case "--connect-timeout":
                    if (Seconds(Next(arg), arg, notes) is { } connect)
                    {
                        options = options with { ConnectTimeout = connect };
                    }

                    break;

                case "--mssp-timeout":
                    if (Seconds(Next(arg), arg, notes) is { } mssp)
                    {
                        options = options with { MsspTimeout = mssp };
                    }

                    break;

                case "--max-hosts":
                    if (Integer(Next(arg), arg, notes) is { } maxHosts)
                    {
                        options = options with { MaxHosts = maxHosts };
                    }

                    break;

                case "--max-duration":
                    if (Minutes(Next(arg), arg, notes) is { } duration)
                    {
                        options = options with { MaxDuration = duration };
                    }

                    break;

                case "--max-depth":
                    if (Integer(Next(arg), arg, notes) is { } depth)
                    {
                        options = options with { MaxDepth = depth };
                    }

                    break;

                case "--no-referrals":
                    options = options with { FollowReferrals = false };
                    break;

                case "--allow-private":
                    options = options with { FollowPrivateAddresses = true };
                    break;

                case "--dry-run":
                    options = options with { DryRun = true };
                    break;

                case "--quiet":
                    break;

                default:
                    return new CommandLineResult { Error = $"Unknown argument \"{arg}\". Try --help." };
            }
        }

        options = options with { Seeds = seeds.Distinct().ToList() };

        try
        {
            options.Validate();
        }
        catch (ArgumentException ex)
        {
            return new CommandLineResult { Error = ex.Message };
        }

        return new CommandLineResult { Options = options, Notes = notes };
    }

    /// <summary>
    /// Merges a JSON defaults file. Unknown keys are ignored rather than rejected: this file is
    /// hand-written, and a typo in one setting should not stop the other twelve from applying — the
    /// resulting configuration is printed at the start of every run, which is where a typo shows up.
    /// </summary>
    private static CrawlOptions ApplyFile(CrawlOptions options, string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return options;
        }

        int? Int(string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var parsed)
                ? parsed
                : null;

        double? Real(string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : null;

        bool? Flag(string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;

        string? Text(string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        return options with
        {
            OutputDirectory = Text("outputDirectory") ?? options.OutputDirectory,
            MaxConcurrency = Int("maxConcurrency") ?? options.MaxConcurrency,
            GlobalInterval = Real("globalIntervalSeconds") is { } g ? TimeSpan.FromSeconds(g) : options.GlobalInterval,
            PerHostInterval = Real("perHostIntervalSeconds") is { } p ? TimeSpan.FromSeconds(p) : options.PerHostInterval,
            ConnectTimeout = Real("connectTimeoutSeconds") is { } c ? TimeSpan.FromSeconds(c) : options.ConnectTimeout,
            MsspTimeout = Real("msspTimeoutSeconds") is { } m ? TimeSpan.FromSeconds(m) : options.MsspTimeout,
            RevisitInterval = Real("revisitHours") is { } r ? TimeSpan.FromHours(r) : options.RevisitInterval,
            NoMsspRevisitInterval = Real("noMsspRevisitHours") is { } n
                ? TimeSpan.FromHours(n)
                : options.NoMsspRevisitInterval,
            RetireAfterFailures = Int("retireAfterFailures") ?? options.RetireAfterFailures,
            MaxHosts = Int("maxHosts") ?? options.MaxHosts,
            MaxDuration = Real("maxDurationMinutes") is { } d ? TimeSpan.FromMinutes(d) : options.MaxDuration,
            MaxDepth = Int("maxDepth") ?? options.MaxDepth,
            FollowReferrals = Flag("followReferrals") ?? options.FollowReferrals,
            FollowPrivateAddresses = Flag("followPrivateAddresses") ?? options.FollowPrivateAddresses,
        };
    }

    private static int IndexOfFlag(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == flag)
            {
                return i;
            }
        }

        return -1;
    }

    private static int? Integer(string? text, string flag, List<string> notes)
    {
        if (text is null)
        {
            return null;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        notes.Add($"{flag}: \"{text}\" is not a number; keeping the default.");
        return null;
    }

    private static TimeSpan? Seconds(string? text, string flag, List<string> notes) =>
        Real(text, flag, notes) is { } value ? TimeSpan.FromSeconds(value) : null;

    private static TimeSpan? Minutes(string? text, string flag, List<string> notes) =>
        Real(text, flag, notes) is { } value ? TimeSpan.FromMinutes(value) : null;

    private static TimeSpan? Hours(string? text, string flag, List<string> notes) =>
        Real(text, flag, notes) is { } value ? TimeSpan.FromHours(value) : null;

    private static double? Real(string? text, string flag, List<string> notes)
    {
        if (text is null)
        {
            return null;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        notes.Add($"{flag}: \"{text}\" is not a number; keeping the default.");
        return null;
    }
}
