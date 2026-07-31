using Microsoft.Extensions.Logging;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Telnet.Mssp;
using SharpMUTerm.Core.Text;
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

        // The one directory this client writes its own files into, resolved once, here, because this is
        // the only code that knows it is the live client. Everything below is handed it rather than
        // reaching for it: an app that is not this entry point — a snapshot, a test — is given none and so
        // writes no transcript at all. See SharpMUTermApp's `logRoot` parameter for the defect that
        // resolving it inside the app caused.
        var logRoot = Path.Combine(Path.GetDirectoryName(ConfigurationStore.DefaultPath)!, "logs");

        // Client diagnostics: an in-memory history behind ⌃P ▸ Show client messages, plus a rolling
        // file beside the session logs but plainly not one of them (client-diagnostics-*.log next to
        // the World.Character-*.log transcripts). Never a console sink — this app owns the screen.
        using var diagnostics = ClientDiagnostics.Create(logRoot);

        // Drain what the configuration load had to say now that there is somewhere to say it. A secrets file
        // that could not be read means characters start with no password — worth one line in the client
        // message log (⌃P), and never more than that: the client still runs, still connects, and the password
        // can simply be typed again.
        var loadLogger = diagnostics.For("SharpMUTerm.Configuration");
        foreach (var notice in loadNotices)
        {
            loadLogger.LogWarning("{Notice}", notice);
        }

        // The panes' own memory between runs, beside the configuration whose LastSession says where each
        // pane goes. Resolved here for the same reason logRoot is: only this code knows it is the live
        // client, so only it hands over a directory to write in. It is created unconditionally even when
        // the feature is off — constructing one touches no disk, and the ⌃P purge has to be able to
        // clear what an earlier, enabled run left behind.
        using var restore = new RestoreLog(
            string.IsNullOrWhiteSpace(config.RestoreLog.Directory)
                ? RestoreLog.DefaultRoot(ConfigurationStore.DefaultPath)
                : config.RestoreLog.Directory!,
            config.RestoreLog)
        {
            Logger = diagnostics.For("SharpMUTerm.RestoreLog"),
        };

        // What every server has said about itself, beside the configuration and deliberately not in it:
        // config.json is what the user asked for and is hand-edited, and a write per connect has no
        // business landing there. Resolved here for the same reason logRoot and the restore log are —
        // only this code knows it is the live client. Anything else gets a memory-only cache and so
        // provably writes nothing.
        var mssp = new MsspCache(MsspCache.PathFor(ConfigurationStore.DefaultPath))
        {
            Logger = diagnostics.For("SharpMUTerm.Mssp"),
        };
        if (mssp.Problem is { } msspProblem)
        {
            loadLogger.LogWarning("{Notice}", msspProblem);
        }

        var liveApp = new SharpMUTermApp(
            config,
            capabilities,
            diagnostics: diagnostics,
            save: saved => ConfigurationStore.Save(ConfigurationStore.DefaultPath, saved),
            logRoot: logRoot,
            restore: restore,
            mssp: mssp);
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

        // Named for the same reason: it is a file this client creates in the user's own directory, and
        // "what is this and can I delete it" should be answerable from the help page. It can: it is a
        // cache of what servers published, and deleting it costs only the next connection's report.
        usage.WriteLine($"Servers: {MsspCache.PathFor(ConfigurationStore.DefaultPath)}"
            + " — each server's last MSSP report (F5 ▸ i). A cache; safe to delete.");
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

        // Alt, not Ctrl, and the page says why: Ctrl+digit is what a reader will try first (it is what was
        // asked for) and it cannot work — no digit has a control byte of its own, so a terminal sends the
        // bare digit, or one already spelt Escape or Backspace. Naming the working chord and the reason
        // the obvious one is absent is the same honesty this page owes everywhere else.
        //
        // It says *window* and not pane. The chord counted panes until a capture window sharing a pane
        // turned out to be the thing people wanted to reach; the numbered pane jump is ⌃B N now, and both
        // are named here so a reader is not left thinking one replaced the other silently.
        usage.WriteLine("Windows: Alt+1..Alt+9 go straight to a numbered window and bring it forward — a character's");
        usage.WriteLine("        own window, a capture window, the web view. The numbers are the ones the sidebar");
        usage.WriteLine("        prints beside each window and each character ('Alt+2', 'Alt+3'...), in the order the");
        usage.WriteLine("        windows were opened. It says so when there is no window with that number.");
        usage.WriteLine("        Ctrl+digit is not offered: no terminal sends a distinct Ctrl+digit — 3 and 8 arrive");
        usage.WriteLine("        as Escape and Backspace, and 1, 9 and 0 as the bare digit.");
        usage.WriteLine("Panes:  Ctrl+B then 1..9 goes to a numbered pane instead — the pane itself, whatever tab it");
        usage.WriteLine("        is showing. Panes are numbered in the order they were created, which is the numbering");
        usage.WriteLine("        Ctrl+O counts in and the one the move overlay (Ctrl+B m) badges each pane with.");

        // Alt+Shift+arrow is a chord this host does deliver — the parser reads both modifier bits out of
        // CSI 1;4 <final> — which is why it may be named here at all; see TerminalKeyArrivalTests. It is
        // deliberately not Ctrl+Shift+arrow, which this page used to name: kitty_mod is ctrl+shift, and
        // kitty consumes ctrl+shift+left/right for its own tabs, so half of that chord never arrived.
        usage.WriteLine("Size:   Alt+Shift+Up/Down/Left/Right make the focused pane taller, shorter, narrower or");
        usage.WriteLine("        wider by one character cell — the arrow says what happens to the pane, wherever it");
        usage.WriteLine("        sits (it says so when there is no split that way, or the pane paying is at its");
        usage.WriteLine("        smallest).");

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
        usage.WriteLine("        Alt+D disconnects it at once. Neither asks. With nothing connected, each says so.");
        usage.WriteLine("        Alt+J and Alt+K move to the next and previous character you have open — the two the");
        usage.WriteLine("        sidebar marks 'Alt+J' and 'Alt+K'. They never open one that is not; the sidebar and");
        usage.WriteLine("        Ctrl+P do that.");
        usage.WriteLine("Panes:  Ctrl+B then | - z o 1-9 x b m i < > splits, zooms, goes and moves; Esc or Ctrl+B");
        usage.WriteLine("        cancels, and pausing after Ctrl+B pops a panel naming each key. Or drag a tab");
        usage.WriteLine("        strip onto another pane — middle drops it as a tab, an edge splits there.");
    }
}
