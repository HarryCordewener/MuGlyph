using System.Text.Json;
using SharpMUTerm.Core.Telnet.Mssp;

namespace SharpMUTerm.Crawler;

/// <summary>One seed file's contents, and whatever could not be read from it.</summary>
public sealed record SeedList(IReadOnlyList<MsspHost> Hosts, IReadOnlyList<string> Rejected);

/// <summary>
/// Where a run gets its starting hosts.
/// <para>
/// <b>This tool reads nothing it was not pointed at.</b> It never opens <c>config.json</c>,
/// <c>secrets.json</c>, or anything else under the user's configuration directory on its own
/// initiative — there is no default path here and no probing for one. A crawler has no business
/// knowing what worlds somebody plays or what passwords they keep, and the safest way to guarantee
/// that is to have no code that could find them.
/// </para>
/// </summary>
public static class Seeds
{
    /// <summary>
    /// Reads a seed file: one host per line, <c>host port</c> as MSSP's own <c>REFERRAL</c> spells it,
    /// with <c>#</c> comments and blank lines ignored. <c>host:port</c> is accepted too, on the same
    /// terms as a referral (see <see cref="MsspHost.TryParse"/>).
    /// <para>
    /// Lines that do not parse are returned rather than thrown, so a run tells the operator which of
    /// their lines was wrong and then gets on with the ones that were right.
    /// </para>
    /// </summary>
    public static SeedList FromFile(string path)
    {
        var hosts = new List<MsspHost>();
        var rejected = new List<string>();
        var seen = new HashSet<MsspHost>();

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            var comment = line.IndexOf('#');
            if (comment >= 0)
            {
                line = line[..comment].Trim();
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (MsspHost.TryParse(line, out var host))
            {
                if (seen.Add(host))
                {
                    hosts.Add(host);
                }
            }
            else
            {
                rejected.Add(raw.Trim());
            }
        }

        return new SeedList(hosts, rejected);
    }

    /// <summary>
    /// Reads seeds from a SharpMUTerm configuration file the operator has explicitly named.
    /// <para>
    /// <b>Only ever from an explicit path, and only ever the host and the port.</b> There is no default
    /// location and no search: the operator has to say <c>--seed-from-worlds &lt;file&gt;</c> and name
    /// the file, because "read the user's config" is not something a crawler should be able to do by
    /// accident. What it takes from that file is two fields per world. It does not read characters,
    /// does not read logins, does not touch <c>secrets.json</c> — which lives beside the configuration
    /// and holds passwords — and does not write anything back.
    /// </para>
    /// <para>
    /// It is parsed as a loose JSON document rather than through the client's own configuration types
    /// on purpose: binding to those would give this tool a way to read every other field in the file,
    /// and the guarantee above is easier to keep when the code cannot see them.
    /// </para>
    /// </summary>
    public static SeedList FromWorldsFile(string path)
    {
        var hosts = new List<MsspHost>();
        var rejected = new List<string>();
        var seen = new HashSet<MsspHost>();

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("worlds", out var worlds) ||
            worlds.ValueKind != JsonValueKind.Array)
        {
            return new SeedList(hosts, ["the file has no \"worlds\" array"]);
        }

        foreach (var world in worlds.EnumerateArray())
        {
            var host = Text(world, "host");
            var port = Number(world, "port");
            var name = Text(world, "name") ?? host ?? "(unnamed world)";

            if (host is null || port is null)
            {
                rejected.Add($"{name}: no host or port");
                continue;
            }

            if (MsspHost.Create(host, port.Value) is { } parsed)
            {
                if (seen.Add(parsed))
                {
                    hosts.Add(parsed);
                }
            }
            else
            {
                rejected.Add($"{name}: {host} {port}");
            }
        }

        return new SeedList(hosts, rejected);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var port)
            ? port
            : null;
}
