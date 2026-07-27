using MuClient.Core.Configuration;

namespace MuClient.Core.Session;

/// <summary>Manages the set of open world sessions (the tabbed multi-world model).</summary>
public sealed class SessionManager : IAsyncDisposable
{
    private readonly List<WorldSession> _sessions = new();
    private readonly object _gate = new();

    public event EventHandler<WorldSession>? SessionAdded;

    public event EventHandler<WorldSession>? SessionRemoved;

    public IReadOnlyList<WorldSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return _sessions.ToArray();
            }
        }
    }

    /// <summary>Creates and registers a session for a world (does not connect it).</summary>
    public WorldSession Open(WorldDefinition world, int scrollbackCapacity = 20_000)
    {
        ArgumentNullException.ThrowIfNull(world);
        var session = new WorldSession(world, scrollbackCapacity: scrollbackCapacity);
        Add(session);
        return session;
    }

    /// <summary>Registers an already-constructed session (used by tests with a fake transport).</summary>
    public void Add(WorldSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            _sessions.Add(session);
        }

        SessionAdded?.Invoke(this, session);
    }

    public async Task CloseAsync(WorldSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        bool removed;
        lock (_gate)
        {
            removed = _sessions.Remove(session);
        }

        if (removed)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            SessionRemoved?.Invoke(this, session);
        }
    }

    public async ValueTask DisposeAsync()
    {
        WorldSession[] snapshot;
        lock (_gate)
        {
            snapshot = _sessions.ToArray();
            _sessions.Clear();
        }

        foreach (var session in snapshot)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}
