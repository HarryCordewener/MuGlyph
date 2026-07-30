using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpMUTerm.Core.Telnet;
using SharpMUTerm.Core.Telnet.Mssp;
using SharpMUTerm.Core.Transport;
using SharpMUTerm.Crawler.Model;

namespace SharpMUTerm.Crawler.Probing;

/// <summary>One visit to one server. Faked in tests; the real one opens a socket.</summary>
public interface IMsspProbe
{
    Task<ProbeResult> ProbeAsync(MsspHost host, CancellationToken cancellationToken);
}

/// <summary>
/// Connects to a server, lets telnet negotiation run, takes the MSSP report, and hangs up.
/// <para>
/// <b>It sends nothing.</b> There is no call to <see cref="ITelnetSession.SendLineAsync"/> anywhere in
/// this file and there must never be one. A crawler has no business logging in, no business issuing a
/// command, and no name to log in with; the only bytes it puts on the wire are the option negotiation
/// the telnet layer emits on its own behalf. That is not a convention — it is what makes this tool a
/// legitimate use of a protocol designed to be read by strangers rather than an unattended robot
/// poking at other people's games. <c>NothingButNegotiationIsSentTests</c> asserts it against the raw
/// outbound bytes.
/// </para>
/// <para>
/// It reuses <see cref="TcpTransport"/> and <see cref="TelnetSession"/> rather than opening a socket
/// and speaking telnet itself, so there is exactly one telnet implementation in this solution and the
/// crawler exercises the same negotiation the client does.
/// </para>
/// </summary>
public sealed class TelnetMsspProbe(
    CrawlOptions options,
    Func<ConnectionOptions, ITransport>? transportFactory = null,
    ILogger? logger = null,
    TimeProvider? time = null) : IMsspProbe
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private readonly Func<ConnectionOptions, ITransport> _transportFactory =
        transportFactory ?? (connection => new TcpTransport(connection));

    public async Task<ProbeResult> ProbeAsync(MsspHost host, CancellationToken cancellationToken)
    {
        var startedAt = _time.GetUtcNow();
        var stopwatch = Stopwatch.StartNew();

        var connection = new ConnectionOptions
        {
            Host = host.Host,
            Port = host.Port,
            ConnectTimeout = options.ConnectTimeout,
        };

        // Completed by the first MSSP report, or left unfinished if the server never sends one.
        var report = new TaskCompletionSource<MsspData>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var session = new TelnetSession(
            _transportFactory(connection),
            _logger,
            new TelnetSessionOptions
            {
                // Say what this is. A server operator reading their log should be able to tell that
                // something identifying itself as an MSSP crawler connected, took the status report and
                // left, and to find out what it was.
                TerminalTypes = options.TerminalTypes,

                // Ask for MSSP rather than waiting to be offered it. Most servers that support MSSP do
                // not volunteer it, and a crawler that only listens records them as having none — which
                // is both wrong and, since it means visiting them again later for nothing, impolite.
                RequestOptions = [TelnetSessionOptions.MsspOption],

                // No keepalive: this connection is measured in seconds and is closed as soon as it has
                // what it came for. Filler traffic on a stranger's socket would be gratuitous.
                KeepaliveInterval = null,
            });

        session.MsspReceived += (_, e) => report.TrySetResult(e.Data);
        session.Disconnected += (_, e) => closed.TrySetResult(e.Error);

        ProbeResult Result(CrawlOutcome outcome, MsspData? data = null, string? error = null) => new()
        {
            Host = host,
            Outcome = outcome,
            ObservedAt = startedAt,
            Duration = stopwatch.Elapsed,
            Data = data,
            Error = error,
        };

        try
        {
            // Cancelled in the finally, so the deadline task never outlives the probe that armed it.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            await session.ConnectAsync(deadline.Token).ConfigureAwait(false);

            if (!session.TerminalTypesApplied)
            {
                // Identifying honestly is a requirement, not a nicety. If the telnet layer could not be
                // told who we are, the connection is closed rather than continued anonymously.
                _logger.LogError("Could not state the crawler's identity to {Host}; abandoning the probe.", host);
                return Result(CrawlOutcome.Error, error: "could not set the client identity");
            }

            // Whichever comes first: the report, the server hanging up, or the deadline.
            var expired = Task.Delay(options.MsspTimeout, _time, deadline.Token);
            var finished = await Task.WhenAny(report.Task, closed.Task, expired).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (finished == report.Task)
            {
                var data = await report.Task.ConfigureAwait(false);
                return Result(CrawlOutcome.MsspReceived, data);
            }

            if (finished == closed.Task)
            {
                // The server closed before offering MSSP. That is an answer, not a fault: most MU*
                // servers do not implement MSSP, and one that hung up on us has told us to go away.
                var error = await closed.Task.ConfigureAwait(false);
                return error is null
                    ? Result(CrawlOutcome.NoMssp)
                    : Result(CrawlOutcome.ConnectFailed, error: Describe(error));
            }

            return Result(CrawlOutcome.NoMssp, error: "no MSSP within the deadline");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result(CrawlOutcome.Timeout, error: "timed out");
        }
        catch (SocketException ex)
        {
            return Result(CrawlOutcome.ConnectFailed, error: Describe(ex));
        }
        catch (IOException ex)
        {
            return Result(CrawlOutcome.ConnectFailed, error: Describe(ex));
        }
        catch (Exception ex)
        {
            return Result(CrawlOutcome.Error, error: Describe(ex));
        }
        finally
        {
            // Leave immediately, whatever happened. Nothing is gained by holding a stranger's socket
            // open past the moment we have what we came for.
            try
            {
                await session.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Closing the connection to {Host} failed.", host);
            }
        }
    }

    /// <summary>
    /// One line, no stack trace, no host name repeated back. These strings are written to a report a
    /// human reads and to a state file that persists, so they stay short.
    /// </summary>
    private static string Describe(Exception error) => error switch
    {
        SocketException socket => socket.SocketErrorCode.ToString(),
        _ => error.GetType().Name,
    };
}
