using Microsoft.Extensions.Logging;
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

        // Anything the load wants to say about the secrets file is collected here and logged once diagnostics
        // exist — the pipeline is built further down, and this runs before it. It is deliberately a list that
        // is drained exactly once: a secrets problem is a startup fact, not a recurring notice.
        var loadNotices = new List<string>();
        var config = LoadConfiguration(loadNotices.Add);
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

            // A snapshot shows your own configuration, like every other way of running the client.
            // `--demo-config` swaps in the built-in demo worlds instead: that is what the docs images
            // and golden frames use, because a golden file that changes with the developer's own
            // worlds isn't one. Opting in keeps the demo where it belongs — an explicit request,
            // never the default state of the app.
            if (args.Contains("--demo-config"))
            {
                config = DemoScene.Build();
            }

            // No save action: a snapshot renders, it does not edit. The settings screens persist each
            // committed change now, and a --view that drives keys into a field would otherwise write the
            // demo worlds straight over the real configuration.
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

        // What this launch connects: the command line's host if one was typed, else whatever is marked
        // `at start` on F5, else nothing at all. The precedence lives in Core (StartupConnections) so it
        // can be asserted without a terminal; the parsing stays here.
        var startup = StartupConnections.Resolve(config, CommandLineWorld(args));

        // Client diagnostics: an in-memory history behind ⌃P ▸ Show client messages, plus a rolling
        // file beside the session logs but plainly not one of them (client-diagnostics-*.log next to
        // the World.Character-*.log transcripts). Never a console sink — this app owns the screen.
        using var diagnostics = ClientDiagnostics.Create(
            Path.Combine(Path.GetDirectoryName(ConfigurationStore.DefaultPath)!, "logs"));

        // Drain what the configuration load had to say now that there is somewhere to say it. A secrets file
        // that could not be read means characters start with no password — worth one line in the client
        // message log (⌃P), and never more than that: the client still runs, still connects, and the password
        // can simply be typed again.
        var loadLogger = diagnostics.For("SharpMUTerm.Configuration");
        foreach (var notice in loadNotices)
        {
            loadLogger.LogWarning("{Notice}", notice);
        }

        var liveApp = new SharpMUTermApp(
            config,
            capabilities,
            diagnostics: diagnostics,
            save: saved => ConfigurationStore.Save(ConfigurationStore.DefaultPath, saved));
        var exitCode = liveApp.Run(startup); // blocks on the SharpConsoleUI main loop until exit

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

    /// <summary>
    /// Loads the configuration, collecting anything the store wants reported into
    /// <paramref name="report"/> for the caller to log once diagnostics exist.
    /// </summary>
    private static AppConfiguration LoadConfiguration(Action<string> report)
    {
        try
        {
            return ConfigurationStore.Load(ConfigurationStore.DefaultPath, report);
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
    /// The world named on the command line — <c>host [port]</c> plus its switches — or null when no host
    /// was given.
    /// <para>
    /// It used to fall back to <c>config.Worlds.FirstOrDefault()</c>, and that fallback was the whole of
    /// the client's startup policy: the first world's first character, dialled unconditionally, with no
    /// way to name a different one and no way to decline. Choosing is now
    /// <see cref="CharacterDefinition.ConnectAtStartup"/> and it belongs in
    /// <see cref="StartupConnections"/>, so this function is left doing only the thing its name says.
    /// </para>
    /// </summary>
    private static WorldDefinition? CommandLineWorld(string[] args)
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

        return null;
    }

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void PrintUsage() => Console.Write(UsageText);

    /// <summary>
    /// The <c>--help</c> text. Built into a string rather than written straight to the console so a test
    /// can hold it to the honesty rule the settings screens are already held to: every key this page names
    /// has to be one that works, and — the half that bit here — it must not name one that cannot fire.
    /// Shift+⏎ and Ctrl+⏎ are the case in point: they are what was asked for and no Unix terminal reports
    /// them distinctly through SharpConsoleUI's input parser, so naming them would send the reader to press
    /// a key that does nothing.
    /// </summary>
    internal static string UsageText
    {
        get
        {
            var text = new StringWriter();
            WriteUsage(text);
            return text.ToString();
        }
    }

    private static void WriteUsage(TextWriter usage)
    {
        usage.WriteLine("SharpMUTerm — a cross-platform TUI MU* client.");
        usage.WriteLine();
        usage.WriteLine("Usage: sharpmuterm [host] [port] [options]");
        usage.WriteLine();
        usage.WriteLine("  host                 Server hostname or IP (IPv4/IPv6).");
        usage.WriteLine("  port                 Server port (default 4000).");
        usage.WriteLine("  --tls                Connect over TLS.");
        usage.WriteLine("  --insecure           Accept invalid TLS certificates.");
        usage.WriteLine("  --name <name>        Display name for the world.");
        usage.WriteLine("  --snapshot           Render one frame (ANSI) headlessly and exit.");
        usage.WriteLine("  --size <WxH>         Snapshot size in cells (default 160x48).");
        usage.WriteLine("  --view <name>        Snapshot an overlay (e.g. 'settings') over the workspace.");
        usage.WriteLine("                       '<name>-edit' opens that settings screen mid field edit.");
        usage.WriteLine("  --demo-config        Snapshot the built-in demo worlds instead of your own.");
        usage.WriteLine("  --out <file>         Write the snapshot to a file instead of stdout.");
        usage.WriteLine("  -h, --help           Show this help.");
        usage.WriteLine();
        usage.WriteLine($"Config: {ConfigurationStore.DefaultPath}");

        // Named because it is the file a user has to know about to look after it — and because "where did my
        // password go" should be answerable without reading the source. Config is safe to share; this is not.
        usage.WriteLine($"Secrets: {SecretsStore.PathFor(ConfigurationStore.DefaultPath)}"
            + " — character passwords, plain text, owner-only. Not the file to paste.");
        // "why does it connect to *that*?" is the question this setting answers, so the answer belongs
        // on the page a user reaches for when they ask it. Both halves are stated: what connects with no
        // host, and that a host overrides it.
        usage.WriteLine("With no host, the characters marked 'at start' on F5 connect — none by default,");
        usage.WriteLine("and the client opens with no connection. A host given here connects instead of them.");
        usage.WriteLine("'at start' only opens the connection. What is typed once one is open follows from the");
        usage.WriteLine("character's saved password and connect line — F5's 'login' row says which.");
        usage.WriteLine();
        usage.WriteLine("In-app: Up/Down history · Ctrl+N next window · Ctrl+W close · Ctrl+P palette · Ctrl+Q quit.");
        usage.WriteLine("Scroll: PgUp/PgDn a page · Shift+Up/Down a line · Ctrl+Home top · Ctrl+End back to live output.");
        usage.WriteLine("Focus:  Ctrl+Left/Right/Up/Down move between panes (Ctrl+Down at the bottom reaches the second");
        usage.WriteLine("        command line); Ctrl+O cycles them; Tab switches command lines. The pane you are on and");
        usage.WriteLine("        the line Enter sends from are both drawn lit, and the focused pane's tab is marked.");

        // Ctrl+Shift+arrow is a chord this host does deliver — the parser reads both modifier bits out of
        // CSI 1;6 <final> — which is why it may be named here at all; see TerminalKeyArrivalTests.
        usage.WriteLine("Size:   Ctrl+Shift+Left/Right/Up/Down resize the focused pane by two character cells "
            + "(it says so when");
        usage.WriteLine("        there is no split that way, or the pane beside it is at its smallest).");

        // Alt+Enter and Ctrl+L only. Shift+Enter and Ctrl+Enter are deliberately not listed: no Unix
        // terminal reports them distinctly through SharpConsoleUI's input parser (both arrive as a bare
        // Enter), and a help page naming a key that cannot fire is the defect this file is careful about.
        usage.WriteLine("Typing: Alt+Enter or Ctrl+L inserts a newline · Ctrl+A/E line ends · Ctrl+K/U kill ·");
        usage.WriteLine("        Alt+Left/Right by word · Ctrl+R searches history.");
        // The connection pair, described by what the key does rather than by what it asks — it asks
        // nothing. Both act at once, on the character whose pane is focused, and "drops and redials" is
        // spelt out because a reconnect on a live connection is a disconnect with a dial after it and the
        // reader has to know that before pressing it.
        usage.WriteLine("World:  Alt+R reconnects the focused character (drops the connection and redials it at once);");
        usage.WriteLine("        Ctrl+D disconnects it at once. Neither asks. With nothing connected, each says so.");
        usage.WriteLine("Panes:  Ctrl+B then | - z o x b m i < > splits, zooms, closes and moves; Esc or Ctrl+B");
        usage.WriteLine("        cancels, and pausing after Ctrl+B pops a panel naming each key. Or drag a tab");
        usage.WriteLine("        strip onto another pane — middle drops it as a tab, an edge splits there.");
    }
}
