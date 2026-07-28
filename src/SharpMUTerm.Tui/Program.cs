using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Graphics;
using SharpConsoleUI.Drivers;

namespace SharpMUTerm.Tui;

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
        var capabilities = DetectCapabilities(config);

        // Headless snapshot: render one demo frame to ANSI (for docs images / CI golden files) and
        // exit, without a terminal or a connection. See tools/ansi_frame_to_image.py.
        if (args.Contains("--snapshot"))
        {
            // Detach stdin before constructing the window system: even with a headless driver it can
            // start reading the console for input, which BLOCKS FOREVER when stdin is an interactive
            // TTY or an open pipe (the frame never renders). A null reader returns EOF immediately, so
            // the snapshot is deterministic however it's launched (terminal, pipe, or CI redirect).
            Console.SetIn(TextReader.Null);

            // With no real config on disk, drive the snapshot off the demo configuration — worlds,
            // trigger sets, and a saved LastSession the app resumes exactly like a returning user's.
            if (config.Worlds.Count == 0)
            {
                config = DemoScene.Build();
            }

            var (width, height) = ParseSize(args);
            var app = new SharpMUTermApp(config, capabilities, new HeadlessConsoleDriver(width, height));
            var frame = app.RenderSnapshot(GetOption(args, "--view"));
            var outPath = GetOption(args, "--out");
            if (outPath is not null)
            {
                File.WriteAllText(outPath, frame);
            }
            else
            {
                Console.Out.Write(frame);
                Console.Out.Flush();
            }

            // The framework keeps foreground worker threads alive; the frame is captured, so exit
            // hard rather than waiting on them (keeps the snapshot fast + deterministic in CI).
            Environment.Exit(0);
        }

        var world = ResolveWorld(args, config);
        var liveApp = new SharpMUTermApp(config, capabilities);
        var exitCode = liveApp.Run(world); // blocks on the SharpConsoleUI main loop until exit

        // Persist the workspace so the next launch resumes where this one left off.
        try
        {
            config.LastSession = liveApp.CaptureSession();
            ConfigurationStore.Save(ConfigurationStore.DefaultPath, config);
        }
        catch
        {
            // A failed save must never change the exit code — the session is a convenience, not critical.
        }

        return exitCode;
    }

    /// <summary>Parses <c>--size WxH</c> (default 160x48) for the snapshot frame.</summary>
    private static (int Width, int Height) ParseSize(string[] args)
    {
        var size = GetOption(args, "--size");
        if (size is not null)
        {
            var parts = size.Split('x', 'X');
            if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
            {
                return (Math.Clamp(w, 20, 400), Math.Clamp(h, 8, 200));
            }
        }

        return (160, 48);
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
        // A config graphics override maps onto the same SHARPMUTERM_GRAPHICS mechanism the probe reads.
        if (!string.IsNullOrEmpty(config.GraphicsOverride))
        {
            Environment.SetEnvironmentVariable("SHARPMUTERM_GRAPHICS", config.GraphicsOverride);
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
        Console.WriteLine("SharpMUTerm — a cross-platform TUI MU* client.");
        Console.WriteLine();
        Console.WriteLine("Usage: sharpmuterm [host] [port] [options]");
        Console.WriteLine();
        Console.WriteLine("  host                 Server hostname or IP (IPv4/IPv6).");
        Console.WriteLine("  port                 Server port (default 4000).");
        Console.WriteLine("  --tls                Connect over TLS.");
        Console.WriteLine("  --insecure           Accept invalid TLS certificates.");
        Console.WriteLine("  --name <name>        Display name for the world.");
        Console.WriteLine("  --snapshot           Render one demo frame (ANSI) headlessly and exit.");
        Console.WriteLine("  --size <WxH>         Snapshot size in cells (default 160x48).");
        Console.WriteLine("  --view <name>        Snapshot an overlay (e.g. 'settings') over the workspace.");
        Console.WriteLine("                       '<name>-edit' opens that settings screen mid field edit.");
        Console.WriteLine("  --out <file>         Write the snapshot to a file instead of stdout.");
        Console.WriteLine("  -h, --help           Show this help.");
        Console.WriteLine();
        Console.WriteLine($"Config: {ConfigurationStore.DefaultPath}");
        Console.WriteLine("With no host, the first configured world is used (if any).");
        Console.WriteLine();
        Console.WriteLine("In-app: Up/Down history · Ctrl+N next window · Ctrl+O next pane · Ctrl+W close · Ctrl+P palette · Ctrl+Q quit.");
        Console.WriteLine("Panes:  drag a pane's tab strip onto another pane — middle drops it as a tab, an edge splits there.");
    }
}
