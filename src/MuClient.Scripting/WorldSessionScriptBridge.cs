using MuClient.Core.Automation;
using MuClient.Core.Session;
using MuClient.Core.Telnet;

namespace MuClient.Scripting;

/// <summary>
/// Adapts a live <see cref="WorldSession"/> to <see cref="IScriptWorld"/> and pumps the session's
/// script-relevant events (trigger fires, GMCP messages) into an attached <see cref="ScriptHost"/>.
/// </summary>
/// <remarks>
/// Alias <em>function</em> callbacks are a known gap: the core <see cref="WorldSession"/> does not
/// currently raise a script event for script-backed aliases, so a bridged
/// <c>alias.add(pattern, function ... end)</c> registers the alias but its callback is never
/// dispatched. String-substitution aliases and all trigger/GMCP callbacks work end to end.
/// </remarks>
public sealed class WorldSessionScriptBridge : IScriptWorld, IDisposable
{
    private readonly WorldSession _session;
    private ScriptHost? _host;
    private bool _disposed;

    public WorldSessionScriptBridge(WorldSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _session.TriggerScriptRequested += OnTriggerScriptRequested;
        _session.GmcpReceived += OnGmcpReceived;
    }

    /// <summary>Constructs a bridge and a host wired to each other for the given session.</summary>
    public static ScriptHost CreateHost(WorldSession session)
    {
        var bridge = new WorldSessionScriptBridge(session);
        var host = new ScriptHost(bridge);
        bridge.Attach(host);
        return host;
    }

    /// <summary>Connects this bridge to the host that will receive dispatched events.</summary>
    public void Attach(ScriptHost host) => _host = host ?? throw new ArgumentNullException(nameof(host));

    public string WorldName => _session.World.Name;

    public void Send(string command) => _ = _session.SendRawAsync(command);

    public void Print(string text) => _session.PrintSystem(text);

    public void AddTrigger(string pattern, string callbackId) =>
        _session.Triggers.Add(new Trigger
        {
            Name = callbackId,
            Pattern = pattern,
            Actions = new TriggerActions { ScriptCallback = callbackId },
        });

    public void AddAlias(string pattern, string substitution, string callbackId) =>
        _session.Aliases.Add(new Alias
        {
            Name = callbackId,
            Pattern = pattern,
            Substitution = substitution,
            ScriptCallback = string.IsNullOrEmpty(callbackId) ? null : callbackId,
        });

    public IDisposable ScheduleEvery(TimeSpan interval, Action callback) =>
        _session.Scheduler.Every(interval, callback);

    public IDisposable ScheduleAfter(TimeSpan delay, Action callback) =>
        _session.Scheduler.After(delay, callback);

    private void OnTriggerScriptRequested(object? sender, TriggerScriptInvocation e)
    {
        var match = e.Match;
        var groups = new string[Math.Max(0, match.Groups.Count - 1)];
        for (var i = 1; i < match.Groups.Count; i++)
        {
            groups[i - 1] = match.Groups[i].Value;
        }

        _host?.DispatchTrigger(e.Callback, match.Value, groups);
    }

    private void OnGmcpReceived(object? sender, GmcpMessageEventArgs e) =>
        _host?.DispatchGmcp(e.Package, e.Json);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.TriggerScriptRequested -= OnTriggerScriptRequested;
        _session.GmcpReceived -= OnGmcpReceived;
    }
}
