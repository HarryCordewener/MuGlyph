using SharpMUTerm.Core.Automation;
using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Session;

namespace SharpMUTerm.Core.Tests.Session;

/// <summary>
/// The F6 screen's timers, asserted as commands actually reaching the server. Before this the
/// <see cref="TimerDefinition"/> list was persisted, edited and never realised by anything: the
/// session's <see cref="IntervalScheduler"/> only ever held script timers, so a configured timer was
/// a row of JSON that fired nowhere.
/// <para>
/// Intervals here are milliseconds-long on purpose, and every wait is a poll with a ceiling rather
/// than a fixed sleep, so the tests are quick without being timing-fragile.
/// </para>
/// </summary>
public class WorldSessionTimerTests
{
    private static WorldDefinition World() => new() { Name = "T", Host = "h", Port = 1 };

    private static (WorldSession Session, FakeTelnetSession Telnet) Create(TriggerSet set)
    {
        var telnet = new FakeTelnetSession();
        var session = new WorldSession(World(), triggerSets: new[] { set }, sessionFactory: _ => telnet);
        return (session, telnet);
    }

    /// <summary>Polls until <paramref name="condition"/> holds or the ceiling passes; returns whether it did.</summary>
    private static async Task<bool> Eventually(Func<bool> condition, int millisecondsCeiling = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(millisecondsCeiling);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    [Test]
    public async Task AnEnabledTimer_SendsItsCommandOnceConnected()
    {
        var set = new TriggerSet();
        set.Timers.Add(new TimerDefinition { Name = "idle", IntervalSeconds = 0.05, Command = "@@idle" });
        var (session, telnet) = Create(set);

        await session.ConnectAsync();

        await Assert.That(await Eventually(() => telnet.SentLines.Contains("@@idle"))).IsTrue();
        await session.DisposeAsync();
    }

    [Test]
    public async Task ADisabledTimer_SendsNothing()
    {
        var set = new TriggerSet();
        set.Timers.Add(new TimerDefinition
        {
            Name = "idle",
            IntervalSeconds = 0.02,
            Command = "@@idle",
            Enabled = false,
        });
        var (session, telnet) = Create(set);

        await session.ConnectAsync();
        await Task.Delay(200);

        await Assert.That(telnet.SentLines.Contains("@@idle")).IsFalse();
        await session.DisposeAsync();
    }

    /// <summary>
    /// The F6 checkbox is read at each firing, not when the schedule is built, so unticking a running
    /// timer stops it without a reconnect. This is the difference between "live" and "applied at
    /// startup" for the one setting on these screens that acts without being provoked.
    /// </summary>
    [Test]
    public async Task FlippingEnabled_StopsAndStartsARunningTimer()
    {
        var timer = new TimerDefinition
        {
            Name = "idle",
            IntervalSeconds = 0.03,
            Command = "@@idle",
            Enabled = false,
        };
        var set = new TriggerSet();
        set.Timers.Add(timer);
        var (session, telnet) = Create(set);

        await session.ConnectAsync();
        await Task.Delay(150);
        await Assert.That(telnet.SentLines.Contains("@@idle")).IsFalse();

        timer.Enabled = true;

        await Assert.That(await Eventually(() => telnet.SentLines.Contains("@@idle"))).IsTrue();
        await session.DisposeAsync();
    }

    /// <summary>The command is read per firing too, so retyping it re-points a live timer.</summary>
    [Test]
    public async Task EditingTheCommand_AppliesToTheNextFiring()
    {
        var timer = new TimerDefinition { Name = "poll", IntervalSeconds = 0.03, Command = "first" };
        var set = new TriggerSet();
        set.Timers.Add(timer);
        var (session, telnet) = Create(set);

        await session.ConnectAsync();
        await Assert.That(await Eventually(() => telnet.SentLines.Contains("first"))).IsTrue();

        timer.Command = "second";

        await Assert.That(await Eventually(() => telnet.SentLines.Contains("second"))).IsTrue();
        await session.DisposeAsync();
    }

    [Test]
    public async Task AOneShotTimer_FiresExactlyOnce()
    {
        var set = new TriggerSet();
        set.Timers.Add(new TimerDefinition
        {
            Name = "greet",
            IntervalSeconds = 0.03,
            Command = "hello",
            OneShot = true,
        });
        var (session, telnet) = Create(set);

        await session.ConnectAsync();
        await Assert.That(await Eventually(() => telnet.SentLines.Contains("hello"))).IsTrue();
        await Task.Delay(200);

        await Assert.That(telnet.SentLines.Count(l => l == "hello")).IsEqualTo(1);
        await session.DisposeAsync();
    }

    /// <summary>A zero (or negative) interval is the definition's own "disabled", and never schedules.</summary>
    [Test]
    public async Task AnIntervalOfZero_IsNotScheduled()
    {
        var set = new TriggerSet();
        set.Timers.Add(new TimerDefinition { Name = "never", IntervalSeconds = 0, Command = "nope" });
        var (session, telnet) = Create(set);

        await session.ConnectAsync();
        await Task.Delay(150);

        await Assert.That(telnet.SentLines.Contains("nope")).IsFalse();
        await session.DisposeAsync();
    }

    /// <summary>
    /// A dropped connection cancels the schedules rather than leaving them ticking against a closed
    /// socket. Asserted on the scheduler's own count, not on the absence of sends: a disconnected
    /// session refuses to send anyway, so "nothing arrived" would pass with the timers still running.
    /// </summary>
    [Test]
    public async Task Disconnecting_CancelsTheTimers()
    {
        var set = new TriggerSet();
        set.Timers.Add(new TimerDefinition { Name = "idle", IntervalSeconds = 0.03, Command = "@@idle" });
        var (session, telnet) = Create(set);

        await session.ConnectAsync();
        await Assert.That(await Eventually(() => telnet.SentLines.Contains("@@idle"))).IsTrue();
        await Assert.That(session.Scheduler.Count).IsEqualTo(1);

        await session.DisconnectAsync();

        await Assert.That(session.Scheduler.Count).IsEqualTo(0);
        await session.DisposeAsync();
    }
}
