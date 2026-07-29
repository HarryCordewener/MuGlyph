namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// A clock and timer source a test moves by hand. It exists so the NAWS rate limit can be asserted
/// instead of waited on: a limiter tested with real time is a test that sleeps for its interval and
/// still races the machine it runs on. <see cref="Advance"/> moves the clock and fires every timer
/// whose due time it passes, on the calling thread, so a trailing flush lands before the next line
/// of the test.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock on by <paramref name="by"/>, firing every timer that comes due.</summary>
    public void Advance(TimeSpan by)
    {
        _now += by;

        // A firing callback may re-arm its own timer (the flush does, when a session is still inside
        // its interval), so fire from a snapshot and let the re-arm wait for the next Advance.
        foreach (var timer in _timers.ToArray())
        {
            timer.FireIfDue(_now);
        }
    }

    private void Forget(ManualTimer timer) => _timers.Remove(timer);

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private DateTimeOffset? _dueAt;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        /// <summary>Arms (or, with an infinite due time, disarms) the timer. Periods are unused here.</summary>
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _owner.GetUtcNow() + dueTime;
            return true;
        }

        public void FireIfDue(DateTimeOffset now)
        {
            if (_dueAt is { } due && now >= due)
            {
                _dueAt = null;
                _callback(_state);
            }
        }

        public void Dispose()
        {
            _dueAt = null;
            _owner.Forget(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
