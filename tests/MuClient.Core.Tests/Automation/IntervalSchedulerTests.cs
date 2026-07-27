using MuClient.Core.Automation;

namespace MuClient.Core.Tests.Automation;

public class IntervalSchedulerTests
{
    [Test]
    public async Task After_FiresOnce()
    {
        using var scheduler = new IntervalScheduler();
        var count = 0;
        scheduler.After(TimeSpan.FromMilliseconds(20), () => Interlocked.Increment(ref count));
        await Task.Delay(120);
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task After_RemovesHandleWhenDone()
    {
        using var scheduler = new IntervalScheduler();
        scheduler.After(TimeSpan.FromMilliseconds(10), () => { });
        await Task.Delay(120);
        await Assert.That(scheduler.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Every_FiresRepeatedly()
    {
        using var scheduler = new IntervalScheduler();
        var count = 0;
        var handle = scheduler.Every(TimeSpan.FromMilliseconds(20), () => Interlocked.Increment(ref count));
        // Poll up to ~2s so the assertion is robust on a loaded CI machine rather than time-boxed.
        var deadline = Environment.TickCount64 + 2000;
        while (Volatile.Read(ref count) < 2 && Environment.TickCount64 < deadline)
        {
            await Task.Delay(20);
        }

        handle.Dispose();
        await Assert.That(count).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task Dispose_CancelsSchedule()
    {
        var scheduler = new IntervalScheduler();
        var count = 0;
        scheduler.Every(TimeSpan.FromMilliseconds(20), () => Interlocked.Increment(ref count));
        scheduler.Dispose();
        var after = count;
        await Task.Delay(100);
        await Assert.That(count).IsEqualTo(after);
    }

    [Test]
    public async Task Every_RejectsNonPositiveInterval()
    {
        using var scheduler = new IntervalScheduler();
        await Assert.That(() => scheduler.Every(TimeSpan.Zero, () => { })).Throws<ArgumentOutOfRangeException>();
    }
}
