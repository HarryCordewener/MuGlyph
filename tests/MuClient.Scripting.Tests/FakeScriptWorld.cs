namespace MuClient.Scripting.Tests;

/// <summary>An in-memory <see cref="IScriptWorld"/> that captures every interaction for assertions.</summary>
internal sealed class FakeScriptWorld : IScriptWorld
{
    public List<string> Sent { get; } = new();

    public List<string> Printed { get; } = new();

    public List<(string Pattern, string CallbackId)> Triggers { get; } = new();

    public List<(string Pattern, string Substitution, string CallbackId)> Aliases { get; } = new();

    public List<FakeTimer> Timers { get; } = new();

    public string WorldName { get; set; } = "TestWorld";

    public void Send(string command) => Sent.Add(command);

    public void Print(string text) => Printed.Add(text);

    public void AddTrigger(string pattern, string callbackId) => Triggers.Add((pattern, callbackId));

    public void AddAlias(string pattern, string substitution, string callbackId) =>
        Aliases.Add((pattern, substitution, callbackId));

    public IDisposable ScheduleEvery(TimeSpan interval, Action callback)
    {
        var timer = new FakeTimer(interval, callback, recurring: true);
        Timers.Add(timer);
        return timer;
    }

    public IDisposable ScheduleAfter(TimeSpan delay, Action callback)
    {
        var timer = new FakeTimer(delay, callback, recurring: false);
        Timers.Add(timer);
        return timer;
    }

    /// <summary>The callback id of the most recently registered trigger.</summary>
    public string LastTriggerId => Triggers[^1].CallbackId;

    /// <summary>The callback id of the most recently registered alias.</summary>
    public string LastAliasId => Aliases[^1].CallbackId;
}

internal sealed class FakeTimer : IDisposable
{
    private readonly Action _callback;

    public FakeTimer(TimeSpan interval, Action callback, bool recurring)
    {
        Interval = interval;
        _callback = callback;
        Recurring = recurring;
    }

    public TimeSpan Interval { get; }

    public bool Recurring { get; }

    public bool Cancelled { get; private set; }

    /// <summary>Simulates the scheduler firing this timer.</summary>
    public void Fire() => _callback();

    public void Dispose() => Cancelled = true;
}
