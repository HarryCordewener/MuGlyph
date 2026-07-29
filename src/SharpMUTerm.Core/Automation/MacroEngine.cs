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
    private readonly List<Macro> _macros = new();
    private readonly object _gate = new();

    public MacroEngine(IEnumerable<Macro>? macros = null)
    {
        if (macros is not null)
        {
            _macros.AddRange(macros);
        }
    }

    public IReadOnlyCollection<Macro> Macros
    {
        get
        {
            lock (_gate)
            {
                return _macros.ToArray();
            }
        }
    }

    /// <summary>Adds a binding, replacing whichever one already holds its key.</summary>
    public void Add(Macro macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        lock (_gate)
        {
            var at = IndexOf(macro.Key);
            if (at >= 0)
            {
                _macros[at] = macro;
            }
            else
            {
                _macros.Add(macro);
            }
        }
    }

    public bool Remove(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            return _macros.RemoveAll(m => Matches(m, key)) > 0;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _macros.Clear();
        }
    }

    /// <summary>
    /// Returns the enabled macro bound to <paramref name="keyDescriptor"/>, or null. Two macros on one
    /// key is a configuration the F4 screen refuses to create, but a hand-edited file can still hold
    /// one: the first wins, the way <see cref="AliasEngine"/>'s first matching pattern does.
    /// </summary>
    public Macro? Resolve(string keyDescriptor)
    {
        ArgumentNullException.ThrowIfNull(keyDescriptor);
        lock (_gate)
        {
            var at = IndexOf(keyDescriptor);
            return at >= 0 && _macros[at].Enabled ? _macros[at] : null;
        }
    }

    private int IndexOf(string key) => _macros.FindIndex(m => Matches(m, key));

    private static bool Matches(Macro macro, string key) =>
        string.Equals(macro.Key, key, StringComparison.OrdinalIgnoreCase);
}
