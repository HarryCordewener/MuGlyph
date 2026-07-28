namespace SharpMUTerm.Core.Automation;

/// <summary>
/// Schedules recurring and one-shot callbacks. Backs script <c>timer.every</c>/<c>timer.after</c>
/// and any client-side periodic work. Each schedule returns an <see cref="IDisposable"/> handle
/// that cancels it; disposing the scheduler cancels everything.
/// </summary>
public sealed class IntervalScheduler : IDisposable
{
    private readonly HashSet<Handle> _handles = new();
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>Invokes <paramref name="callback"/> every <paramref name="interval"/> until cancelled.</summary>
    public IDisposable Every(TimeSpan interval, Action callback, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be positive.");
        }

        return Schedule(callback, interval, interval, name);
    }

    /// <summary>Invokes <paramref name="callback"/> once after <paramref name="delay"/>.</summary>
    public IDisposable After(TimeSpan delay, Action callback, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), "Delay must be non-negative.");
        }

        return Schedule(callback, delay, Timeout.InfiniteTimeSpan, name);
    }

    /// <summary>The number of live schedules.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _handles.Count;
            }
        }
    }

    private IDisposable Schedule(Action callback, TimeSpan due, TimeSpan period, string? name)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var handle = new Handle(this, name, period == Timeout.InfiniteTimeSpan);
            handle.Timer = new Timer(
                _ =>
                {
                    try
                    {
                        callback();
                    }
                    catch
                    {
                        // A throwing callback must never escape onto the ThreadPool thread and
                        // crash the process, nor stop a recurring schedule. Callers that need to
                        // observe errors (e.g. the script host) guard their own callbacks.
                    }
                    finally
                    {
                        if (handle.OneShot)
                        {
                            handle.Dispose();
                        }
                    }
                },
                null,
                due,
                period);

            _handles.Add(handle);
            return handle;
        }
    }

    private void Unregister(Handle handle)
    {
        lock (_gate)
        {
            _handles.Remove(handle);
        }
    }

    public void Dispose()
    {
        Handle[] snapshot;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            snapshot = _handles.ToArray();
            _handles.Clear();
        }

        foreach (var handle in snapshot)
        {
            handle.DisposeTimerOnly();
        }
    }

    private sealed class Handle(IntervalScheduler owner, string? name, bool oneShot) : IDisposable
    {
        public Timer? Timer { get; set; }

        public string? Name { get; } = name;

        public bool OneShot { get; } = oneShot;

        public void Dispose()
        {
            DisposeTimerOnly();
            owner.Unregister(this);
        }

        public void DisposeTimerOnly()
        {
            Timer?.Dispose();
            Timer = null;
        }
    }
}
