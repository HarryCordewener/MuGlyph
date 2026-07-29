using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Logging;

namespace SharpMUTerm.Core.Session;

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

    /// <summary>
    /// Creates and registers an anonymous session for a world (no character, no automation).
    /// Used for ad-hoc command-line connections and for a world that has no characters configured.
    /// Does not connect it.
    /// </summary>
    public WorldSession Open(
        WorldDefinition world,
        int scrollbackCapacity = 20_000,
        TextSettings? text = null,
        InputSettings? input = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        var session = new WorldSession(
            world, scrollbackCapacity: scrollbackCapacity, text: text, input: input);
        Add(session);
        return session;
    }

    /// <summary>
    /// Creates and registers a session for a specific character on a world, composing its
    /// automation from <paramref name="triggerSets"/>. Does not connect it.
    /// <para>
    /// <paramref name="text"/> and <paramref name="input"/> are the app-wide F7/F8 preferences and
    /// are passed by reference, not copied — see the <see cref="WorldSession"/> constructor for why
    /// that is the whole point of them.
    /// </para>
    /// </summary>
    public WorldSession Open(
        WorldDefinition world,
        CharacterDefinition character,
        IReadOnlyList<TriggerSet> triggerSets,
        int scrollbackCapacity = 20_000,
        ILogSink? log = null,
        TextSettings? text = null,
        InputSettings? input = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(triggerSets);
        var session = new WorldSession(
            world,
            character,
            triggerSets,
            log: log,
            scrollbackCapacity: scrollbackCapacity,
            text: text,
            input: input);
        Add(session);
        return session;
    }

    /// <summary>Finds an open session by its <see cref="WorldSession.SessionKey"/>, or null.</summary>
    public WorldSession? Find(string sessionKey)
    {
        ArgumentNullException.ThrowIfNull(sessionKey);
        lock (_gate)
        {
            return _sessions.FirstOrDefault(s => s.SessionKey == sessionKey);
        }
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
