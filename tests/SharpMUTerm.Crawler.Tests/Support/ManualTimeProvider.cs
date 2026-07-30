namespace SharpMUTerm.Crawler.Tests.Support;

/// <summary>
/// A clock and timer source a test moves by hand.
/// <para>
/// It exists so a rate limit can be <em>asserted</em> rather than waited for. A limiter tested with
/// real time is a test that sleeps for its own interval and then races the machine it runs on: slow
/// enough to be annoying, flaky enough to be disabled, and it proves nothing about the interval it did
/// not sleep for. <see cref="Advance"/> moves the clock and fires every timer it passes, on the calling
/// thread, so the effect of a wait lands before the next line of the test.
/// </para>
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private readonly Lock _gate = new();

    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock on by <paramref name="by"/>, firing every timer that comes due.</summary>
    public void Advance(TimeSpan by)
    {
        ManualTimer[] due;
        lock (_gate)
        {
            _now += by;
            // A firing callback may arm another timer, so fire from a snapshot and let anything new
            // wait for the next Advance.
            due = [.. _timers];
        }

        var now = GetUtcNow();
        foreach (var timer in due)
        {
            timer.FireIfDue(now);
        }
    }

    private void Forget(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset? _dueAt;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime;
            return true;
        }

        public void FireIfDue(DateTimeOffset now)
        {
            if (_dueAt is { } due && now >= due)
            {
                _dueAt = null;
                callback(state);
            }
        }

        public void Dispose()
        {
            _dueAt = null;
            owner.Forget(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// A clock that runs on demand: a delay does not wait, it moves the clock forward by the amount asked
/// for and returns.
/// <para>
/// This is what lets the crawl <em>loop</em> — which really does wait on its rate limiter — be tested
/// without sleeping. <see cref="ManualTimeProvider"/> cannot serve here: the loop decides how long to
/// wait from inside itself, so a test cannot know how far to advance the clock without reimplementing
/// the thing it is testing. Here time passes exactly as the code under test asks it to, and
/// <see cref="GetUtcNow"/> afterwards is the true elapsed total — which is what the time-cap test
/// asserts against.
/// </para>
/// </summary>
internal sealed class VirtualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();

    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    /// <summary>Moves the clock without any timer being involved — how a test sets up "a day later".</summary>
    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _now += by;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        => new InstantTimer(this, callback, state, dueTime);

    private sealed class InstantTimer : ITimer
    {
        private readonly CancellationTokenSource _cancel = new();

        public InstantTimer(VirtualTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime)
        {
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                return;
            }

            // Queued rather than run inline: Task.Delay builds its timer inside its own constructor, and
            // completing it before that returns deadlocks. The clock still moves by the full amount, so
            // the code under test observes exactly the interval it asked to wait.
            _ = Task.Run(async () =>
            {
                await Task.Yield();
                if (_cancel.IsCancellationRequested)
                {
                    return;
                }

                owner.Advance(dueTime);
                callback(state);
            });
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => _cancel.Cancel();

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
