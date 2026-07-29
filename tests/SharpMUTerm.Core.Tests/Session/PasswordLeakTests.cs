using System.Text;
using Microsoft.Extensions.Logging;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Logging;
using SharpMUTerm.Core.Session;
using SharpMUTerm.Core.Telnet;
using SharpMUTerm.Core.Tests.Telnet;
using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Tests.Session;

/// <summary>
/// Where a password can escape to, asserted at each surface. The login line is the one string this
/// client sends that is worth keeping out of a file, and it now flows through more code than it used to
/// — so each place it passes is pinned rather than reasoned about.
/// <para>
/// Every test here uses one distinctive value and searches for <em>that</em>, so a passing assertion is
/// evidence about the secret and not about the shape of a line. Each also proves the login actually
/// happened, because "the password is nowhere" is trivially true of a client that never sent it.
/// </para>
/// </summary>
public class PasswordLeakTests
{
    /// <summary>
    /// The one value under test. Distinctive enough that finding it anywhere is unambiguous, and not a
    /// substring of any word the client, the telnet stack or the log format uses.
    /// </summary>
    private const string Secret = "zvxq-leak-canary-71";

    private sealed class RecordingSink : ILogSink
    {
        public List<string> Lines { get; } = new();

        public void WriteLine(StyledLine line) => Lines.Add(line.Text);

        public void WriteSystem(string text) => Lines.Add(text);

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Captures everything logged, at every level, message and structured state alike — the shape of the
    /// pipeline the app actually installs (<c>ClientDiagnostics</c>) with its level switch turned all the
    /// way up, which is what the ⌃P viewer's <c>Trace</c> setting does at runtime.
    /// </summary>
    private sealed class RecordingLogger : ILogger
    {
        private readonly object _gate = new();
        private readonly List<string> _entries = new();

        public IReadOnlyList<string> Entries
        {
            get
            {
                lock (_gate)
                {
                    return _entries.ToArray();
                }
            }
        }

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            Record(state.ToString());
            return new Scope();
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Record(formatter(state, exception));

            // The rendered message is not the only thing a sink can see: Serilog writes structured
            // properties out too, so the state's own rendering is captured as well.
            Record(state?.ToString());
            Record(exception?.ToString());
        }

        private void Record(string? text)
        {
            if (text is null)
            {
                return;
            }

            lock (_gate)
            {
                _entries.Add(text);
            }
        }

        private sealed class Scope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private static WorldDefinition World() => new() { Name = "T", Host = "h", Port = 1, LocalEcho = true };

    private static CharacterDefinition Character() => new()
    {
        Name = "Corvid",
        Password = Secret,
        AutoLogin = true,
        OnConnect = "look",
    };

    /// <summary>
    /// The auto-login path, which is the one this feature exists to send people down: the resolved line
    /// reaches telnet and nothing else. It goes out through <c>SendRawAsync</c>, which does not echo and
    /// does not write to the transcript — so the scrollback and the session log see the connect banner and
    /// the on-connect commands, and never the password.
    /// </summary>
    [Test]
    public async Task AutoLogin_SendsThePasswordToTelnetAndNowhereElse()
    {
        var log = new RecordingSink();
        var telnet = new FakeTelnetSession();
        var session = new WorldSession(World(), Character(), sessionFactory: _ => telnet, log: log);

        await session.ConnectAsync();

        // It logged in — otherwise everything below would pass by doing nothing.
        await Assert.That(telnet.SentLines).Contains($"connect Corvid {Secret}");
        await Assert.That(telnet.SentLines).Contains("look");

        await Assert.That(session.Scrollback.Snapshot().Any(l => l.Text.Contains(Secret, StringComparison.Ordinal)))
            .IsFalse();
        await Assert.That(log.Lines.Any(l => l.Contains(Secret, StringComparison.Ordinal))).IsFalse();

        // The log did record the session, so "nothing leaked" is not "nothing was logged".
        await Assert.That(log.Lines).IsNotEmpty();
    }

    /// <summary>
    /// The asymmetry that justifies the whole feature, pinned so nobody closes the gap from the wrong end.
    /// A user who types <c>connect Corvid …</c> by hand has it echoed into the output window and written
    /// into the transcript on disk, because that is what local echo and session logging are <em>for</em>.
    /// Auto-login is the way out of that, not a reason to start filtering the command line.
    /// </summary>
    [Test]
    public async Task TypingTheLoginLineByHandIsEchoedAndTranscribed_WhichIsWhyTheFieldExists()
    {
        var log = new RecordingSink();
        var telnet = new FakeTelnetSession();
        var session = new WorldSession(World(), sessionFactory: _ => telnet, log: log);
        await session.ConnectAsync();

        await session.SendUserInputAsync($"connect Corvid {Secret}");

        await Assert.That(telnet.SentLines).Contains($"connect Corvid {Secret}");
        await Assert.That(session.Scrollback.Snapshot().Any(l => l.Text.Contains(Secret, StringComparison.Ordinal)))
            .IsTrue();
        await Assert.That(log.Lines.Any(l => l.Contains(Secret, StringComparison.Ordinal))).IsTrue();
    }

    /// <summary>
    /// The client diagnostics file, which is the surface most likely to have grown a leak by accident: a
    /// real <c>ILogger</c> is wired into <see cref="TelnetSession"/> now, TelnetNegotiationCore traces per
    /// byte and per state transition, and the ⌃P viewer can raise the level to <c>Trace</c> at runtime.
    /// If the <em>outbound</em> path logged its bytes, the resolved login line would land in
    /// <c>client-diagnostics-*.log</c>.
    /// <para>
    /// It does not: the trace is on the receive side (<c>Processing byte #…</c>, <c>Writing into
    /// buffer</c>) and the send path writes to the network without logging. This is asserted against the
    /// real interpreter rather than read out of its source, so it is a fact about the version actually
    /// referenced — and it fails loudly if a future one starts tracing what it sends.
    /// </para>
    /// </summary>
    [Test]
    public async Task TheTelnetStackNeverLogsWhatItSends_EvenAtTrace()
    {
        var logger = new RecordingLogger();
        var transport = new FakeTransport();
        await using var session = new TelnetSession(transport, logger);
        await session.ConnectAsync();

        // Inbound traffic first, so the trace is demonstrably running: without this the "no secret in the
        // log" assertion could pass on a logger that was never called at all.
        transport.FeedInbound(Encoding.ASCII.GetBytes("What is your password?\r\n"));
        await Assert.That(await WaitUntilAsync(() => logger.Entries.Count > 0)).IsTrue();

        await session.SendLineAsync($"connect Corvid {Secret}");
        var sent = await WaitUntilAsync(() =>
            Encoding.ASCII.GetString(transport.SentBytes).Contains(Secret, StringComparison.Ordinal));
        await Assert.That(sent).IsTrue().Because("the login line has to have gone out for this to mean anything");

        var leaked = logger.Entries.Where(e => e.Contains(Secret, StringComparison.Ordinal)).ToArray();
        await Assert.That(leaked).IsEmpty();

        await session.DisconnectAsync();
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        return condition();
    }
}
