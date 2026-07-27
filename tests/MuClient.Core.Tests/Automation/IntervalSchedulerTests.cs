using MuClient.Core.Automation;

namespace MuClient.Core.Tests.Automation;

public class IntervalSchedulerTests
{
    private static async Task<bool> WaitAsync(Task task, int timeoutMs = 5000) =>
        await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false) == task;

    private static async Task<bool> PollAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(15).ConfigureAwait(false);
        }

        return condition();
    }

    [Test]
    public async Task After_FiresOnce()
    {
        using var scheduler = new IntervalScheduler();
        var count = 0;
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.After(TimeSpan.FromMilliseconds(20), () =>
        {
            Interlocked.Increment(ref count);
            fired.TrySetResult();
        });

        await Assert.That(await WaitAsync(fired.Task)).IsTrue();
        // Give a one-shot timer a chance to (wrongly) fire again before asserting exactly one.
        await Task.Delay(80);
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task After_RemovesHandleWhenDone()
    {
        using var scheduler = new IntervalScheduler();
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.After(TimeSpan.FromMilliseconds(10), () => fired.TrySetResult());
        await WaitAsync(fired.Task);
        await Assert.That(await PollAsync(() => scheduler.Count == 0)).IsTrue();
    }

    [Test]
    public async Task Every_FiresRepeatedly()
    {
        using var scheduler = new IntervalScheduler();
        var count = 0;
        var handle = scheduler.Every(TimeSpan.FromMilliseconds(20), () => Interlocked.Increment(ref count));
        await PollAsync(() => Volatile.Read(ref count) >= 2);
        handle.Dispose();
        await Assert.That(count).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task CallbackException_DoesNotEscapeOrStopScheduler()
    {
        using var scheduler = new IntervalScheduler();
        var survived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ticks = 0;
        scheduler.Every(TimeSpan.FromMilliseconds(15), () =>
        {
            if (Interlocked.Increment(ref ticks) == 1)
            {
                throw new InvalidOperationException("boom");
            }

            survived.TrySetResult();
        });

        // A throwing callback must not crash the process nor prevent later ticks.
        await Assert.That(await WaitAsync(survived.Task)).IsTrue();
    }

    [Test]
    public async Task Dispose_CancelsSchedule()
    {
        var scheduler = new IntervalScheduler();
        var count = 0;
        scheduler.Every(TimeSpan.FromMilliseconds(20), () => Interlocked.Increment(ref count));
        scheduler.Dispose();
        var after = Volatile.Read(ref count);
        await Task.Delay(120);
        await Assert.That(count).IsEqualTo(after);
    }

    [Test]
    public async Task Every_RejectsNonPositiveInterval()
    {
        using var scheduler = new IntervalScheduler();
        await Assert.That(() => scheduler.Every(TimeSpan.Zero, () => { })).Throws<ArgumentOutOfRangeException>();
    }
}
