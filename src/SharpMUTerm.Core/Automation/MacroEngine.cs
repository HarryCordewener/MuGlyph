namespace SharpMUTerm.Core.Automation;

/// <summary>Maps normalised key descriptors to <see cref="Macro"/> bindings.</summary>
public sealed class MacroEngine
{
    private readonly Dictionary<string, Macro> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public MacroEngine(IEnumerable<Macro>? macros = null)
    {
        if (macros is not null)
        {
            foreach (var macro in macros)
            {
                _byKey[macro.Key] = macro;
            }
        }
    }

    public IReadOnlyCollection<Macro> Macros
    {
        get
        {
            lock (_gate)
            {
                return _byKey.Values.ToArray();
            }
        }
    }

    /// <summary>Adds or replaces the binding for a key.</summary>
    public void Add(Macro macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        lock (_gate)
        {
            _byKey[macro.Key] = macro;
        }
    }

    public bool Remove(string key)
    {
        lock (_gate)
        {
            return _byKey.Remove(key);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _byKey.Clear();
        }
    }

    /// <summary>Returns the enabled macro bound to <paramref name="keyDescriptor"/>, or null.</summary>
    public Macro? Resolve(string keyDescriptor)
    {
        ArgumentNullException.ThrowIfNull(keyDescriptor);
        lock (_gate)
        {
            return _byKey.TryGetValue(keyDescriptor, out var macro) && macro.Enabled ? macro : null;
        }
    }
}
