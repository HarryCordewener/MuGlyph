using System.Collections;

namespace SharpMUTerm.Graphics;

/// <summary>
/// Detects the best available inline-graphics protocol from environment variables.
///
/// A headless sandbox cannot perform interactive terminal DA/query handshakes, so we
/// rely on environment heuristics only. Detection is pure over an injected environment
/// so it can be unit-tested exhaustively; <see cref="DetectFromEnvironment"/> is the
/// convenience that reads the real process environment.
/// </summary>
public static class CapabilityProbe
{
    /// <summary>Explicit override: forces a specific protocol regardless of heuristics.</summary>
    public const string OverrideVariable = "MUGLYPH_GRAPHICS";

    // Terminals known to speak Sixel that do not otherwise advertise it via TERM.
    private static readonly string[] KnownSixelTermPrograms =
    {
        "mlterm",
        "yaft",
        "foot",
        "contour",
        "mintty",
        "wezterm",
    };

    /// <summary>
    /// Detects capabilities from an explicit environment map (pure and testable).
    /// Values may be null; lookups are case-sensitive on the key as POSIX env vars are.
    /// </summary>
    public static TerminalCapabilities Detect(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var term = Get(environment, "TERM");
        var termProgram = Get(environment, "TERM_PROGRAM");
        var colorTerm = Get(environment, "COLORTERM");

        var supportsTrueColor =
            Equals(colorTerm, "truecolor") || Equals(colorTerm, "24bit");

        var supportsKitty =
            Contains(term, "kitty") ||
            !string.IsNullOrEmpty(Get(environment, "KITTY_WINDOW_ID")) ||
            Equals(termProgram, "ghostty") ||
            Equals(termProgram, "WezTerm");

        var supportsSixel =
            Contains(term, "sixel") ||
            Equals(Get(environment, "MUGLYPH_SIXEL"), "1") ||
            IsKnownSixelProgram(termProgram);

        // An explicit override wins over everything, but we still report the individual
        // capability flags we detected so callers can reason about fallbacks.
        var overrideValue = Get(environment, OverrideVariable);
        if (TryParseOverride(overrideValue, out var forced))
        {
            return new TerminalCapabilities(forced, supportsTrueColor, supportsKitty, supportsSixel);
        }

        var protocol = supportsKitty
            ? GraphicsProtocol.Kitty
            : supportsSixel
                ? GraphicsProtocol.Sixel
                : supportsTrueColor
                    ? GraphicsProtocol.HalfBlock
                    : GraphicsProtocol.None;

        return new TerminalCapabilities(protocol, supportsTrueColor, supportsKitty, supportsSixel);
    }

    /// <summary>Detects capabilities from the current process environment.</summary>
    public static TerminalCapabilities DetectFromEnvironment() => Detect(ReadProcessEnvironment());

    /// <summary>Snapshots the process environment into a plain dictionary.</summary>
    public static IReadOnlyDictionary<string, string?> ReadProcessEnvironment()
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
            {
                result[key] = entry.Value as string;
            }
        }

        return result;
    }

    private static bool TryParseOverride(string? value, out GraphicsProtocol protocol)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "none":
                protocol = GraphicsProtocol.None;
                return true;
            case "halfblock":
            case "half-block":
                protocol = GraphicsProtocol.HalfBlock;
                return true;
            case "sixel":
                protocol = GraphicsProtocol.Sixel;
                return true;
            case "kitty":
                protocol = GraphicsProtocol.Kitty;
                return true;
            default:
                protocol = GraphicsProtocol.None;
                return false;
        }
    }

    private static bool IsKnownSixelProgram(string? termProgram)
    {
        if (string.IsNullOrEmpty(termProgram))
        {
            return false;
        }

        foreach (var known in KnownSixelTermPrograms)
        {
            if (string.Equals(termProgram, known, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? Get(IReadOnlyDictionary<string, string?> environment, string key) =>
        environment.TryGetValue(key, out var value) ? value : null;

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static bool Equals(string? value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
