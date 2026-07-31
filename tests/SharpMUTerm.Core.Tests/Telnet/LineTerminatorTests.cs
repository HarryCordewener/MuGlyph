using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Session;
using SharpMUTerm.Core.Telnet;

namespace SharpMUTerm.Core.Tests.Telnet;

/// <summary>
/// One line sent is one line on the wire.
/// <para>
/// <see cref="TelnetSession.SendLineAsync"/> used to append <c>\r\n</c> to the text before handing it to
/// <c>TelnetInterpreter.SendAsync</c> — which is itself a <em>line</em> send and appends the terminator.
/// Every line this client sent therefore went out as <c>&lt;text&gt;\r\n\r\n</c>: a spurious empty command
/// after every real one, on every connection, for every typed command, every macro, every timer and the
/// login line.
/// </para>
/// <para>
/// <b>It was visible and had been misread as the server's doing.</b> A MU* connect screen treats the empty
/// line as a command like any other, and a server that redisplays its banner for an unrecognised one did so
/// on every connect — which is why the welcome screen appears twice in the session transcripts before a
/// login that in fact succeeded.
/// </para>
/// <para>
/// These assert on the <em>bytes the transport received</em> rather than on a return value, because
/// <see cref="TelnetSession.SendLineAsync"/> returns as soon as the send is handed off: "it sent" can be
/// true while nothing correct reached the wire.
/// </para>
/// </summary>
public class LineTerminatorTests
{
    private const string Line = "connect Mannaz hunter2";

    /// <summary>Everything written after the opening negotiation, decoded.</summary>
    private static async Task<string> WireAfter(Func<TelnetSession, Task> act)
    {
        var transport = new ScriptedTransport();
        await using var session = new TelnetSession(transport, NullLogger.Instance);
        await session.ConnectAsync();
        await act(session);

        // The send is handed to the interpreter, which writes on its own path; give it a moment to land.
        for (var i = 0; i < 50 && !Encoding.ASCII.GetString(transport.Sent).Contains(Line, StringComparison.Ordinal); i++)
        {
            await Task.Delay(20);
        }

        return Encoding.ASCII.GetString(transport.Sent);
    }

    [Test]
    public async Task ALineIsTerminatedOnceAndNotTwice()
    {
        var wire = await WireAfter(session => session.SendLineAsync(Line).AsTask());

        await Assert.That(wire).EndsWith(Line + "\r\n")
            .Because("the interpreter appends the terminator; adding a second one sends an empty command after it");
        await Assert.That(wire).DoesNotContain(Line + "\r\n\r\n");
    }

    /// <summary>
    /// And the same at the seam that matters most: the login line, which is the one send a user cannot
    /// see, cannot retype, and is sent at a connect screen where a stray empty line is a command.
    /// </summary>
    [Test]
    public async Task TheLoginLineIsTerminatedOnceAndNotTwice()
    {
        var transport = new ScriptedTransport();
        var world = new WorldDefinition { Name = "Convergence MUSH", Host = "game.example.org", Port = 10000 };
        var character = new CharacterDefinition { Name = "Mannaz", Password = "hunter2" };
        world.Characters.Add(character);

        await using var session = new WorldSession(
            world,
            character,
            sessionFactory: _ => new TelnetSession(transport, NullLogger.Instance));

        await session.ConnectAsync();

        const string login = "connect Mannaz hunter2";
        for (var i = 0; i < 50 && !Encoding.ASCII.GetString(transport.Sent).Contains(login, StringComparison.Ordinal); i++)
        {
            await Task.Delay(20);
        }

        var wire = Encoding.ASCII.GetString(transport.Sent);
        await Assert.That(wire).Contains(login + "\r\n");
        await Assert.That(wire).DoesNotContain(login + "\r\n\r\n")
            .Because("an empty line after the login is a command the connect screen answers, and it made every server redisplay its banner");
    }
}
