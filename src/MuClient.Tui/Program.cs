using MuClient.Core.Configuration;
using MuClient.Graphics;
using Terminal.Gui.App;

namespace MuClient.Tui;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        var config = LoadConfiguration();
        var world = ResolveWorld(args, config);
        var capabilities = DetectCapabilities(config);

        Application.Init(null!);
        try
        {
            var app = new MuGlyphApp(config, capabilities);

            // Kick off the connection once the main loop is running so event marshaling works.
            Application.Invoke(() => _ = app.StartAsync(world));

            Application.Run(app.Window, null!);
            return 0;
        }
        finally
        {
            Application.Shutdown();
        }
    }

    private static AppConfiguration LoadConfiguration()
    {
        try
        {
            return ConfigurationStore.Load(ConfigurationStore.DefaultPath);
        }
        catch
        {
            return new AppConfiguration();
        }
    }

    private static TerminalCapabilities DetectCapabilities(AppConfiguration config)
    {
        // A config graphics override maps onto the same MUGLYPH_GRAPHICS mechanism the probe reads.
        if (!string.IsNullOrEmpty(config.GraphicsOverride))
        {
            Environment.SetEnvironmentVariable("MUGLYPH_GRAPHICS", config.GraphicsOverride);
        }

        return CapabilityProbe.DetectFromEnvironment();
    }

    /// <summary>
    /// Resolves the world to connect: explicit host[/port] from the command line, otherwise the
    /// first configured world, otherwise none (the UI starts disconnected).
    /// </summary>
    private static WorldDefinition? ResolveWorld(string[] args, AppConfiguration config)
    {
        var positional = args.Where(a => !a.StartsWith('-')).ToArray();
        if (positional.Length >= 1)
        {
            var host = positional[0];
            var port = positional.Length >= 2 && int.TryParse(positional[1], out var p) ? p : 4000;
            return new WorldDefinition
            {
                Name = GetOption(args, "--name") ?? host,
                Host = host,
                Port = port,
                UseTls = args.Contains("--tls"),
                AllowInvalidCertificates = args.Contains("--insecure"),
            };
        }

        return config.Worlds.FirstOrDefault();
    }

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("MuGlyph — a cross-platform TUI MU* client.");
        Console.WriteLine();
        Console.WriteLine("Usage: muglyph [host] [port] [options]");
        Console.WriteLine();
        Console.WriteLine("  host                 Server hostname or IP (IPv4/IPv6).");
        Console.WriteLine("  port                 Server port (default 4000).");
        Console.WriteLine("  --tls                Connect over TLS.");
        Console.WriteLine("  --insecure           Accept invalid TLS certificates.");
        Console.WriteLine("  --name <name>        Display name for the world.");
        Console.WriteLine("  -h, --help           Show this help.");
        Console.WriteLine();
        Console.WriteLine($"Config: {ConfigurationStore.DefaultPath}");
        Console.WriteLine("With no host, the first configured world is used (if any).");
        Console.WriteLine();
        Console.WriteLine("In-app: PgUp/PgDn scroll · Up/Down history · Tab complete · Ctrl+Q quit.");
    }
}
