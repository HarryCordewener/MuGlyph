using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SharpMUTerm.Core.Diagnostics;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace SharpMUTerm.Tui;

/// <summary>
/// The client's diagnostics pipeline, and the only place in the app that names Serilog. Everything
/// else — this app, <c>WorldSession</c>, and TelnetNegotiationCore inside <c>TelnetSession</c> — logs
/// through <see cref="Microsoft.Extensions.Logging.ILogger"/>, so the implementation is swappable and
/// nothing outside composition depends on it.
/// <para>
/// Two sinks, and deliberately <strong>never a console one</strong>: this app owns the screen, and a
/// console sink would write escape sequences into the middle of the compositor's output and wreck the
/// display. What there is instead:
/// <list type="bullet">
/// <item>an <em>in-memory</em> buffer (<see cref="ClientMessageLog"/>, capped) behind the ⌃P
/// <c>Show client messages</c> viewer, so a status-line notice that dismissed itself is still findable
/// and so is whatever the telnet stack said around it;</item>
/// <item>a <em>rolling file</em> beside the session logs but plainly not one of them — they are
/// <c>World.Character-timestamp.log</c> roleplay transcripts a user keeps, this is
/// <c>client-diagnostics-<em>date</em>.log</c>. They never share a file, and diagnostics never go
/// through <c>PrintSystem</c>, which would put client chrome into a transcript.</item>
/// </list>
/// </para>
/// <para>
/// <see cref="MinimumLevel"/> is a live switch rather than a build-time filter because the debugging
/// workflow is "turn tracing on, reproduce it, look": the telnet interpreter logs at
/// <see cref="LogLevel.Trace"/> per byte and per state transition, so the default is
/// <see cref="LogLevel.Information"/> — unfiltered trace would fill the buffer with NOP transitions and
/// make the viewer useless — and the viewer itself can raise and lower it.
/// </para>
/// </summary>
internal sealed class ClientDiagnostics : IDisposable
{
    /// <summary>The category the app's own messages are logged under.</summary>
    internal const string ClientCategory = "SharpMUTerm.Client";

    /// <summary>How many diagnostics files are kept before the oldest is removed.</summary>
    private const int RetainedFiles = 5;

    private readonly LoggingLevelSwitch _levelSwitch;
    private readonly Serilog.Core.Logger _serilog;
    private readonly ILoggerFactory _factory;

    private ClientDiagnostics(ClientMessageLog messages, string? filePath, LogLevel minimum)
    {
        Messages = messages;
        FilePath = filePath;
        _levelSwitch = new LoggingLevelSwitch(ToSerilog(minimum));

        var configuration = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(_levelSwitch)
            .WriteTo.Sink(new ClientMessageSink(messages));

        if (filePath is not null)
        {
            configuration = configuration.WriteTo.File(
                filePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFiles,
                shared: false);
        }

        _serilog = configuration.CreateLogger();
        _factory = new Serilog.Extensions.Logging.SerilogLoggerFactory(_serilog, dispose: false);
        Logger = _factory.CreateLogger(ClientCategory);
    }

    /// <summary>The in-memory history the ⌃P viewer reads.</summary>
    public ClientMessageLog Messages { get; }

    /// <summary>The rolling diagnostics file, or null when this pipeline is memory-only.</summary>
    public string? FilePath { get; }

    /// <summary>The logger the app's own messages go through.</summary>
    public ILogger Logger { get; }

    /// <summary>Creates a logger for a library category (the telnet stack gets one of these).</summary>
    public ILogger For(string category) => _factory.CreateLogger(category);

    /// <summary>
    /// The level below which nothing is recorded, live-adjustable: the viewer raises it to
    /// <see cref="LogLevel.Trace"/> to watch a negotiation and drops it back afterwards.
    /// </summary>
    public LogLevel MinimumLevel
    {
        get => FromSerilog(_levelSwitch.MinimumLevel);
        set => _levelSwitch.MinimumLevel = ToSerilog(value);
    }

    /// <summary>
    /// A memory-only pipeline: the buffer, no file. What a headless test or a snapshot gets, because
    /// neither should leave a diagnostics file behind on the machine that ran it.
    /// </summary>
    public static ClientDiagnostics InMemory(LogLevel minimum = LogLevel.Information) =>
        new(new ClientMessageLog(), filePath: null, minimum);

    /// <summary>
    /// The real pipeline: the buffer plus a rolling <c>client-diagnostics-*.log</c> in
    /// <paramref name="folder"/>. A folder that cannot be created degrades to memory-only — diagnostics
    /// failing is never a reason for the client not to start.
    /// </summary>
    public static ClientDiagnostics Create(string folder, LogLevel minimum = LogLevel.Information)
    {
        try
        {
            Directory.CreateDirectory(folder);
            return new ClientDiagnostics(
                new ClientMessageLog(), Path.Combine(folder, "client-diagnostics-.log"), minimum);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var memory = InMemory(minimum);
            memory.Logger.LogWarning("Client diagnostics are memory-only: {Reason}", ex.Message);
            return memory;
        }
    }

    public void Dispose()
    {
        _factory.Dispose();
        _serilog.Dispose();
    }

    private static LogEventLevel ToSerilog(LogLevel level) => level switch
    {
        LogLevel.Trace => LogEventLevel.Verbose,
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Warning => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        LogLevel.Critical or LogLevel.None => LogEventLevel.Fatal,
        _ => LogEventLevel.Information,
    };

    private static LogLevel FromSerilog(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => LogLevel.Trace,
        LogEventLevel.Debug => LogLevel.Debug,
        LogEventLevel.Warning => LogLevel.Warning,
        LogEventLevel.Error => LogLevel.Error,
        LogEventLevel.Fatal => LogLevel.Critical,
        _ => LogLevel.Information,
    };

    /// <summary>
    /// The in-memory sink: every log event, ours and the libraries', lands in one ordered
    /// <see cref="ClientMessageLog"/> so the viewer shows a single history rather than two.
    /// </summary>
    private sealed class ClientMessageSink : ILogEventSink
    {
        private readonly ClientMessageLog _log;

        public ClientMessageSink(ClientMessageLog log) => _log = log;

        public void Emit(LogEvent logEvent)
        {
            var severity = logEvent.Level switch
            {
                LogEventLevel.Error or LogEventLevel.Fatal => MessageSeverity.Error,
                LogEventLevel.Warning => MessageSeverity.Warning,
                _ => MessageSeverity.Info,
            };

            var text = logEvent.RenderMessage();
            if (logEvent.Exception is { } ex)
            {
                text = $"{text} — {ex.Message}";
            }

            _log.Record(logEvent.Timestamp.ToUniversalTime(), severity, text, Source(logEvent));
        }

        /// <summary>The logger category an event came from, shortened to its last dotted segment.</summary>
        private static string? Source(LogEvent logEvent)
        {
            if (!logEvent.Properties.TryGetValue("SourceContext", out var value))
            {
                return null;
            }

            var context = value.ToString().Trim('"');
            var dot = context.LastIndexOf('.');
            return dot >= 0 && dot < context.Length - 1 ? context[(dot + 1)..] : context;
        }
    }
}
