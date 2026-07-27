using MuClient.Core.Theming;

namespace MuClient.Core.Configuration;

/// <summary>Top-level MuGlyph configuration: global preferences plus the saved worlds.</summary>
public sealed class AppConfiguration
{
    /// <summary>Schema version, for future migrations.</summary>
    public int Version { get; set; } = 1;

    /// <summary>The name of the active built-in theme (see <see cref="ThemeLibrary"/>).</summary>
    public string ThemeName { get; set; } = "Dark";

    /// <summary>
    /// The active theme's colours. Defaults to the dark theme; editing these fields customises
    /// the theme in place. Set <see cref="ThemeName"/> to a built-in name to reset.
    /// </summary>
    public Theme Theme { get; set; } = ThemeLibrary.Dark();

    /// <summary>Maximum scrollback lines retained per world.</summary>
    public int ScrollbackLines { get; set; } = 20_000;

    /// <summary>
    /// Forces a graphics protocol regardless of capability detection: one of
    /// <c>none</c>, <c>halfblock</c>, <c>sixel</c>, <c>kitty</c>. Null means auto-detect.
    /// </summary>
    public string? GraphicsOverride { get; set; }

    /// <summary>Default charset preference order (IANA names), most-preferred first.</summary>
    public List<string> CharsetOrder { get; set; } = new() { "utf-8", "iso-8859-1" };

    public List<WorldDefinition> Worlds { get; set; } = new();
}
