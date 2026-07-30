namespace SharpMUTerm.Core.Automation;

/// <summary>
/// Resolves normalised key descriptors to <see cref="Macro"/> bindings.
/// <para>
/// It holds the macros themselves and reads <see cref="Macro.Key"/> on every lookup. It used to be a
/// <c>Dictionary</c> keyed on that string, which was a cache of a value the F4 screen can now change:
/// rebinding a macro left the dictionary answering to the key the macro no longer carried, invisibly,
/// until the next reconnect rebuilt the engine. The same reason <see cref="Trigger.Pattern"/> drops its
/// compiled matcher on write — except that here there is nothing to drop, because there is nothing
/// derived left to hold.
/// </para>
/// </summary>
public sealed class MacroEngine
{
    private readonly List<Macro> _configured = new();
    private readonly List<Macro> _runtime = new();
    private readonly object _gate = new();

    public MacroEngine(IEnumerable<Macro>? macros = null)
    {
        if (macros is not null)
        {
            _configured.AddRange(macros);
        }
    }

    /// <summary>
    /// Points the engine at the bindings the configuration holds now, leaving <see cref="Add"/>'s alone.
    /// See <see cref="TriggerEngine.ReplaceConfigured"/> for why this is a push and not a read-through; it
    /// is what makes a keypad binding added on F4 mid-connection reach the next keystroke.
    /// </summary>
    public void ReplaceConfigured(IEnumerable<Macro> macros)
    {
        ArgumentNullException.ThrowIfNull(macros);
        var replacement = macros.ToArray();
        lock (_gate)
        {
            _configured.Clear();
            _configured.AddRange(replacement);
        }
    }

    /// <summary>Configured bindings first, in the order F4 shows them, then the runtime ones.</summary>
    public IReadOnlyCollection<Macro> Macros
    {
        get
        {
            lock (_gate)
            {
                var all = new List<Macro>(_configured.Count + _runtime.Count);
                all.AddRange(_configured);
                all.AddRange(_runtime);
                return all;
            }
        }
    }

    /// <summary>
    /// Adds a runtime binding, replacing whichever runtime one already holds its key. It also
    /// <em>shadows</em> a configured binding on that key — see <see cref="Resolve"/> — which is what
    /// "replacing whichever one holds it" means now that the configured list is reloadable.
    /// </summary>
    public void Add(Macro macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        lock (_gate)
        {
            var at = IndexOf(_runtime, macro.Key);
            if (at >= 0)
            {
                _runtime[at] = macro;
            }
            else
            {
                _runtime.Add(macro);
            }
        }
    }

    public bool Remove(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            var removed = _runtime.RemoveAll(m => Matches(m, key));
            removed += _configured.RemoveAll(m => Matches(m, key));
            return removed > 0;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _configured.Clear();
            _runtime.Clear();
        }
    }

    /// <summary>
    /// Returns the enabled macro bound to <paramref name="keyDescriptor"/>, or null. Two macros on one
    /// key is a configuration the F4 screen refuses to create, but a hand-edited file can still hold
    /// one: the first wins, the way <see cref="AliasEngine"/>'s first matching pattern does.
    /// <para>
    /// A runtime binding (<see cref="Add"/>) is looked at first and shadows the configured one on that
    /// key completely — including when it is disabled, because a script that turned a binding off meant
    /// that key, not "fall through to whatever the file says".
    /// </para>
    /// </summary>
    public Macro? Resolve(string keyDescriptor)
    {
        ArgumentNullException.ThrowIfNull(keyDescriptor);
        lock (_gate)
        {
            foreach (var list in new[] { _runtime, _configured })
            {
                var at = IndexOf(list, keyDescriptor);
                if (at >= 0)
                {
                    return list[at].Enabled ? list[at] : null;
                }
            }

            return null;
        }
    }

    private static int IndexOf(List<Macro> macros, string key) => macros.FindIndex(m => Matches(m, key));

    private static bool Matches(Macro macro, string key) =>
        string.Equals(macro.Key, key, StringComparison.OrdinalIgnoreCase);
}
