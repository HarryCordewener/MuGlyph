using System.Text;
using SharpMUTerm.Core.Telnet;

namespace SharpMUTerm.Core.Tests.Telnet;

public class TelnetSessionTests
{
    private const byte IAC = 255;
    private const byte WILL = 251;
    private const byte GA = 249;
    private const byte EOR = 239;
    private const byte NAWS = 31;
    private const byte ECHO = 1;

    /// <summary>Collects output events and lets a test await a matching one.</summary>
    private sealed class OutputWaiter
    {
        private readonly List<TelnetOutputEventArgs> _events = new();
        private readonly object _gate = new();
        private TaskCompletionSource? _tcs;
        private Func<TelnetOutputEventArgs, bool>? _predicate;

        public void Attach(ITelnetSession session) => session.OutputReceived += (_, e) =>
        {
            lock (_gate)
            {
                _events.Add(e);
                if (_predicate?.Invoke(e) == true)
                {
                    _tcs?.TrySetResult();
                }
            }
        };

        public async Task<TelnetOutputEventArgs> WaitAsync(Func<TelnetOutputEventArgs, bool> predicate, int timeoutMs = 3000)
        {
            Task task;
            lock (_gate)
            {
                var existing = _events.FirstOrDefault(predicate);
                if (existing is not null)
                {
                    return existing;
                }

                _predicate = predicate;
                _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                task = _tcs.Task;
            }

            var completed = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (completed != task)
            {
                throw new TimeoutException("Timed out waiting for a matching output event.");
            }

            lock (_gate)
            {
                return _events.First(predicate);
            }
        }
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

            await Task.Delay(15).ConfigureAwait(false);
        }

        return condition();
    }

    [Test]
    public async Task Connect_AdvertisesNaws()
    {
        var transport = new FakeTransport();
        await using var session = new TelnetSession(transport);
        await session.ConnectAsync();

        // The client proactively offers NAWS: IAC WILL NAWS.
        var advertised = await WaitUntilAsync(() => Contains(transport.SentBytes, IAC, WILL, NAWS));
        await Assert.That(advertised).IsTrue();
        await session.DisconnectAsync();
    }

    [Test]
    public async Task ServerLine_IsSurfacedAsOutput()
    {
        var transport = new FakeTransport();
        await using var session = new TelnetSession(transport);
        var waiter = new OutputWaiter();
        waiter.Attach(session);
        await session.ConnectAsync();

        transport.FeedInbound(Encoding.ASCII.GetBytes("Hello world\r\n"));

        var evt = await waiter.WaitAsync(e => e.Text == "Hello world");
        await Assert.That(evt.IsPrompt).IsFalse();
        await session.DisconnectAsync();
    }

    [Test]
    public async Task MultipleLines_AreSurfacedInOrder()
    {
        var transport = new FakeTransport();
        await using var session = new TelnetSession(transport);
        var waiter = new OutputWaiter();
        waiter.Attach(session);
        await session.ConnectAsync();

        transport.FeedInbound(Encoding.ASCII.GetBytes("one\r\ntwo\r\nthree\r\n"));

        var evt = await waiter.WaitAsync(e => e.Text == "three");
        await Assert.That(evt.Text).IsEqualTo("three");
        await session.DisconnectAsync();
    }

    [Test]
    public async Task EmbeddedIacNegotiation_DoesNotLeakIntoOutput()
    {
        var transport = new FakeTransport();
        await using var session = new TelnetSession(transport);
        var waiter = new OutputWaiter();
        waiter.Attach(session);
        await session.ConnectAsync();

        var data = new List<byte>();
        data.AddRange(Encoding.ASCII.GetBytes("visible"));
        data.AddRange(new byte[] { IAC, WILL, ECHO });
        data.AddRange(Encoding.ASCII.GetBytes("text\r\n"));
        transport.FeedInbound(data.ToArray());

        var evt = await waiter.WaitAsync(e => e.Text.Contains("visible"));
        await Assert.That(evt.Text).IsEqualTo("visibletext");
        await Assert.That(evt.Text).DoesNotContain("ÿ");
        await session.DisconnectAsync();
    }

    [Test]
    public async Task Prompt_TerminatedByEor_IsSurfacedAsPrompt()
    {
        var transport = new FakeTransport();
        await using var session = new TelnetSession(transport);
        var waiter = new OutputWaiter();
        waiter.Attach(session);
        await session.ConnectAsync();

        // Server enables EOR (IAC WILL EOR = 255 251 25), then sends a prompt ended by IAC EOR.
        transport.FeedInbound(IAC, WILL, 25);
        await Task.Delay(50);
        var prompt = new List<byte>();
        prompt.AddRange(Encoding.ASCII.GetBytes("Enter name: "));
        prompt.AddRange(new byte[] { IAC, EOR });
        transport.FeedInbound(prompt.ToArray());

        var evt = await waiter.WaitAsync(e => e.IsPrompt);
        await Assert.That(evt.Text).IsEqualTo("Enter name: ");
        await session.DisconnectAsync();
    }

    [Test]
    public async Task SendLine_WritesCommandToTransport()
    {
        var transport = new FakeTransport();
        await using var session = new TelnetSession(transport);
        await session.ConnectAsync();

        await session.SendLineAsync("look");

        var written = await WaitUntilAsync(() =>
            Encoding.ASCII.GetString(transport.SentBytes).Contains("look\r\n"));
        await Assert.That(written).IsTrue();
        await session.DisconnectAsync();
    }

    [Test]
    public async Task EndOfStream_RaisesDisconnected()
    {
        var transport = new FakeTransport();
        await using var session = new TelnetSession(transport);
        var disconnected = new TaskCompletionSource<SessionDisconnectedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Disconnected += (_, e) => disconnected.TrySetResult(e);
        await session.ConnectAsync();

        transport.CompleteInbound();

        var completed = await Task.WhenAny(disconnected.Task, Task.Delay(3000));
        await Assert.That(completed == disconnected.Task).IsTrue();
        await Assert.That((await disconnected.Task).IsClean).IsTrue();
    }

    private static bool Contains(byte[] haystack, params byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
