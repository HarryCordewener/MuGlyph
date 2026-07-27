using System.Globalization;
using System.Text;
using MoonSharp.Interpreter;

namespace MuClient.Scripting;

/// <summary>
/// Hosts a single sandboxed Lua environment for a world and bridges it to an
/// <see cref="IScriptWorld"/>. Owns the MoonSharp <see cref="Script"/>, injects the client API
/// (<c>world</c>, <c>output</c>, <c>trigger</c>, <c>alias</c>, <c>timer</c>, <c>gmcp</c>,
/// <c>log</c>), and routes world events (trigger fires, GMCP messages) back into Lua callbacks.
/// </summary>
/// <remarks>
/// <para><b>Sandbox.</b> The module set is hand-picked (see <see cref="SandboxModules"/>): no
/// <c>io</c>, no <c>os.execute</c>/<c>os.exit</c>, and no <c>require</c>/<c>dofile</c>/<c>loadfile</c>
/// (the whole <c>LoadMethods</c> module is excluded). <c>os.time</c>/<c>os.date</c>, string, math,
/// table, coroutine, and json are available.</para>
/// <para><b>Calling conventions.</b> A trigger callback is invoked as
/// <c>fn(wholeMatch, group1, group2, ...)</c> — the full matched text followed by each capture
/// group in order. An alias function callback uses the same shape. A GMCP handler is invoked as
/// <c>fn(json)</c> with the raw JSON payload string; a handler registered for package <c>"Char"</c>
/// also fires for sub-packages such as <c>"Char.Vitals"</c>.</para>
/// <para><b>Threading.</b> Timer callbacks arrive on thread-pool threads; every Lua invocation is
/// serialised under an internal gate since MoonSharp scripts are not thread-safe.</para>
/// </remarks>
public sealed class ScriptHost : IDisposable
{
    private const CoreModules SandboxModules =
        CoreModules.Basic |
        CoreModules.GlobalConsts |
        CoreModules.TableIterators |
        CoreModules.Metatables |
        CoreModules.String |
        CoreModules.Table |
        CoreModules.ErrorHandling |
        CoreModules.Math |
        CoreModules.Coroutine |
        CoreModules.Bit32 |
        CoreModules.OS_Time |
        CoreModules.Json;

    private readonly IScriptWorld _world;
    private readonly object _gate = new();
    private readonly Dictionary<string, DynValue> _triggerCallbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DynValue> _aliasCallbacks = new(StringComparer.Ordinal);
    private readonly List<(string Package, DynValue Handler)> _gmcpHandlers = new();
    private readonly List<IDisposable> _timers = new();
    private readonly List<string> _loadedFiles = new();

    private Script _script = null!;
    private int _callbackSeed;
    private bool _disposed;

    public ScriptHost(IScriptWorld world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        ResetScript();
    }

    /// <summary>Raised when a Lua callback (trigger/alias/timer/gmcp) throws at runtime.</summary>
    public event EventHandler<ScriptException>? Error;

    /// <summary>The files currently tracked for hot-reload, in load order.</summary>
    public IReadOnlyList<string> LoadedFiles
    {
        get
        {
            lock (_gate)
            {
                return _loadedFiles.ToArray();
            }
        }
    }

    /// <summary>Runs a chunk of Lua for its side effects. Errors surface as <see cref="ScriptException"/>.</summary>
    public void Execute(string luaSource)
    {
        ArgumentNullException.ThrowIfNull(luaSource);
        Evaluate(luaSource);
    }

    /// <summary>Runs a chunk of Lua and returns its value. Errors surface as <see cref="ScriptException"/>.</summary>
    public DynValue Evaluate(string luaSource)
    {
        ArgumentNullException.ThrowIfNull(luaSource);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                return _script.DoString(luaSource);
            }
            catch (InterpreterException ex)
            {
                throw ScriptException.FromInterpreter(ex);
            }
        }
    }

    /// <summary>Loads and executes a Lua file, tracking it for <see cref="Reload"/>.</summary>
    public void LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = Path.GetFullPath(path);
        string source;
        try
        {
            source = File.ReadAllText(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ScriptException($"Could not read script file '{full}': {ex.Message}", null, ex);
        }

        lock (_gate)
        {
            if (!_loadedFiles.Contains(full, StringComparer.Ordinal))
            {
                _loadedFiles.Add(full);
            }
        }

        Execute(source);
    }

    /// <summary>
    /// Discards all script-registered callbacks and timers, rebuilds a fresh sandbox, and
    /// re-executes every file previously passed to <see cref="LoadFile"/>. Inline
    /// <see cref="Execute"/> state is not retained across a reload.
    /// </summary>
    public void Reload()
    {
        string[] files;
        lock (_gate)
        {
            files = _loadedFiles.ToArray();
            ResetScript();
        }

        foreach (var file in files)
        {
            string source;
            try
            {
                source = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new ScriptException($"Could not read script file '{file}': {ex.Message}", null, ex);
            }

            Execute(source);
        }
    }

    /// <summary>Delivers a trigger fire into Lua. No-op if the callback id is unknown (e.g. post-reload).</summary>
    public void DispatchTrigger(string callbackId, string wholeMatch, IReadOnlyList<string> groups)
    {
        ArgumentNullException.ThrowIfNull(callbackId);
        DynValue fn;
        lock (_gate)
        {
            if (_disposed || !_triggerCallbacks.TryGetValue(callbackId, out fn!))
            {
                return;
            }
        }

        var args = new object[1 + (groups?.Count ?? 0)];
        args[0] = wholeMatch ?? string.Empty;
        if (groups is not null)
        {
            for (var i = 0; i < groups.Count; i++)
            {
                args[i + 1] = groups[i] ?? string.Empty;
            }
        }

        InvokeCallback(fn, args);
    }

    /// <summary>Delivers an alias fire into Lua. No-op if the callback id is unknown.</summary>
    public void DispatchAlias(string callbackId, string wholeMatch, IReadOnlyList<string> groups)
    {
        ArgumentNullException.ThrowIfNull(callbackId);
        DynValue fn;
        lock (_gate)
        {
            if (_disposed || !_aliasCallbacks.TryGetValue(callbackId, out fn!))
            {
                return;
            }
        }

        var args = new object[1 + (groups?.Count ?? 0)];
        args[0] = wholeMatch ?? string.Empty;
        if (groups is not null)
        {
            for (var i = 0; i < groups.Count; i++)
            {
                args[i + 1] = groups[i] ?? string.Empty;
            }
        }

        InvokeCallback(fn, args);
    }

    /// <summary>Delivers a GMCP message into Lua, firing every handler whose package matches.</summary>
    public void DispatchGmcp(string package, string json)
    {
        ArgumentNullException.ThrowIfNull(package);
        (string Package, DynValue Handler)[] handlers;
        lock (_gate)
        {
            if (_disposed || _gmcpHandlers.Count == 0)
            {
                return;
            }

            handlers = _gmcpHandlers.ToArray();
        }

        foreach (var (registered, handler) in handlers)
        {
            if (PackageMatches(registered, package))
            {
                InvokeCallback(handler, json ?? string.Empty);
            }
        }
    }

    private static bool PackageMatches(string registered, string incoming) =>
        string.Equals(registered, incoming, StringComparison.OrdinalIgnoreCase) ||
        incoming.StartsWith(registered + ".", StringComparison.OrdinalIgnoreCase);

    private void InvokeCallback(DynValue fn, params object[] args)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _script.Call(fn, args);
            }
        }
        catch (InterpreterException ex)
        {
            Error?.Invoke(this, ScriptException.FromInterpreter(ex));
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, new ScriptException(ex.Message, null, ex));
        }
    }

    private void ResetScript()
    {
        foreach (var timer in _timers)
        {
            timer.Dispose();
        }

        _timers.Clear();
        _triggerCallbacks.Clear();
        _aliasCallbacks.Clear();
        _gmcpHandlers.Clear();

        _script = new Script(SandboxModules);
        InjectApi(_script);
    }

    private string NextCallbackId(string prefix)
    {
        var n = Interlocked.Increment(ref _callbackSeed);
        return $"{prefix}#{n.ToString(CultureInfo.InvariantCulture)}";
    }

    private void InjectApi(Script script)
    {
        var globals = script.Globals;

        // world.*
        var world = new Table(script)
        {
            ["send"] = (Action<string>)(cmd => _world.Send(cmd ?? string.Empty)),
            ["print"] = (Action<string>)(text => _world.Print(text ?? string.Empty)),
            ["name"] = _world.WorldName,
        };
        globals["world"] = world;

        // output.*
        var output = new Table(script)
        {
            ["print"] = (Action<string>)(text => _world.Print(text ?? string.Empty)),
            // Styling is not yet modelled through IScriptWorld; the options table is ignored and
            // the text is printed plain. See the limitations note in the type/PR summary.
            ["printStyled"] = (Action<string, DynValue>)((text, _) => _world.Print(text ?? string.Empty)),
        };
        globals["output"] = output;

        // trigger.*
        var trigger = new Table(script)
        {
            ["add"] = (Action<string, DynValue>)AddTrigger,
        };
        globals["trigger"] = trigger;

        // alias.*
        var alias = new Table(script)
        {
            ["add"] = (Action<string, DynValue>)AddAlias,
        };
        globals["alias"] = alias;

        // timer.*
        var timer = new Table(script)
        {
            ["every"] = (Func<double, DynValue, DynValue>)((ms, fn) => AddTimer(ms, fn, recurring: true)),
            ["after"] = (Func<double, DynValue, DynValue>)((ms, fn) => AddTimer(ms, fn, recurring: false)),
        };
        globals["timer"] = timer;

        // gmcp.*
        var gmcp = new Table(script)
        {
            ["on"] = (Action<string, DynValue>)OnGmcp,
        };
        globals["gmcp"] = gmcp;

        // log.*
        var log = new Table(script)
        {
            ["info"] = (Action<string>)(msg => _world.Print("[info] " + (msg ?? string.Empty))),
            ["warn"] = (Action<string>)(msg => _world.Print("[warn] " + (msg ?? string.Empty))),
            ["error"] = (Action<string>)(msg => _world.Print("[error] " + (msg ?? string.Empty))),
        };
        globals["log"] = log;

        // Override the default print (which writes to Console) so it routes to the client output.
        globals["print"] = DynValue.NewCallback((_, callArgs) =>
        {
            var sb = new StringBuilder();
            for (var i = 0; i < callArgs.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append('\t');
                }

                sb.Append(callArgs[i].ToPrintString());
            }

            _world.Print(sb.ToString());
            return DynValue.Nil;
        });
    }

    private void AddTrigger(string pattern, DynValue fn)
    {
        RequireFunction(fn, "trigger.add");
        var id = NextCallbackId("trigger");
        lock (_gate)
        {
            _triggerCallbacks[id] = fn;
        }

        _world.AddTrigger(pattern ?? string.Empty, id);
    }

    private void AddAlias(string pattern, DynValue substitutionOrFn)
    {
        if (substitutionOrFn is null || substitutionOrFn.IsNil())
        {
            throw new ScriptRuntimeException("alias.add: expected a substitution string or a function.");
        }

        if (substitutionOrFn.Type == DataType.Function)
        {
            var id = NextCallbackId("alias");
            lock (_gate)
            {
                _aliasCallbacks[id] = substitutionOrFn;
            }

            _world.AddAlias(pattern ?? string.Empty, string.Empty, id);
            return;
        }

        if (substitutionOrFn.Type == DataType.String)
        {
            _world.AddAlias(pattern ?? string.Empty, substitutionOrFn.String, string.Empty);
            return;
        }

        throw new ScriptRuntimeException(
            $"alias.add: expected a string or function, got {substitutionOrFn.Type.ToLuaTypeString()}.");
    }

    private DynValue AddTimer(double milliseconds, DynValue fn, bool recurring)
    {
        RequireFunction(fn, recurring ? "timer.every" : "timer.after");
        if (double.IsNaN(milliseconds) || milliseconds < 0)
        {
            throw new ScriptRuntimeException("timer interval must be a non-negative number of milliseconds.");
        }

        // Interval schedulers reject a zero recurring period; clamp to a 1ms minimum.
        var span = TimeSpan.FromMilliseconds(recurring && milliseconds <= 0 ? 1 : milliseconds);
        void Fire() => InvokeCallback(fn);

        var handle = recurring ? _world.ScheduleEvery(span, Fire) : _world.ScheduleAfter(span, Fire);
        lock (_gate)
        {
            _timers.Add(handle);
        }

        var table = new Table(_script);
        table["cancel"] = DynValue.NewCallback((_, _) =>
        {
            handle.Dispose();
            lock (_gate)
            {
                _timers.Remove(handle);
            }

            return DynValue.Nil;
        });
        return DynValue.NewTable(table);
    }

    private void OnGmcp(string package, DynValue fn)
    {
        RequireFunction(fn, "gmcp.on");
        if (string.IsNullOrEmpty(package))
        {
            throw new ScriptRuntimeException("gmcp.on: package name must be a non-empty string.");
        }

        lock (_gate)
        {
            _gmcpHandlers.Add((package, fn));
        }
    }

    private static void RequireFunction(DynValue fn, string who)
    {
        if (fn is null || fn.Type != DataType.Function)
        {
            throw new ScriptRuntimeException($"{who}: expected a function callback.");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var timer in _timers)
            {
                timer.Dispose();
            }

            _timers.Clear();
            _triggerCallbacks.Clear();
            _aliasCallbacks.Clear();
            _gmcpHandlers.Clear();
        }
    }
}
