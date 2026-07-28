namespace SharpMUTerm.Core.Text;

/// <summary>Event payload describing lines appended to a <see cref="ScrollbackBuffer"/>.</summary>
public sealed class LinesAppendedEventArgs(IReadOnlyList<StyledLine> lines) : EventArgs
{
    public IReadOnlyList<StyledLine> Lines { get; } = lines;
}

/// <summary>
/// A bounded, thread-safe ring buffer of terminal output lines. Old lines are evicted
/// once <see cref="Capacity"/> is exceeded. UI-agnostic: the view subscribes to
/// <see cref="LinesAppended"/> and reads windows via <see cref="GetRange"/>.
/// </summary>
public sealed class ScrollbackBuffer
{
    private readonly LinkedList<StyledLine> _lines = new();
    private readonly object _gate = new();

    public ScrollbackBuffer(int capacity = 20_000)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be at least 1.");
        }

        Capacity = capacity;
    }

    /// <summary>Maximum number of retained lines.</summary>
    public int Capacity { get; }

    /// <summary>Raised after one or more lines are appended.</summary>
    public event EventHandler<LinesAppendedEventArgs>? LinesAppended;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _lines.Count;
            }
        }
    }

    /// <summary>Appends a single line, evicting the oldest if at capacity.</summary>
    public void Append(StyledLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
        {
            AppendLocked(line);
        }

        LinesAppended?.Invoke(this, new LinesAppendedEventArgs(new[] { line }));
    }

    /// <summary>Appends a batch of lines, raising a single event.</summary>
    public void AppendRange(IReadOnlyList<StyledLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var line in lines)
            {
                AppendLocked(line);
            }
        }

        LinesAppended?.Invoke(this, new LinesAppendedEventArgs(lines));
    }

    /// <summary>Returns a snapshot copy of a window of lines by absolute index from the oldest.</summary>
    public IReadOnlyList<StyledLine> GetRange(int start, int count)
    {
        if (start < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        lock (_gate)
        {
            var result = new List<StyledLine>(Math.Min(count, Math.Max(0, _lines.Count - start)));
            var index = 0;
            foreach (var line in _lines)
            {
                if (index >= start + count)
                {
                    break;
                }

                if (index >= start)
                {
                    result.Add(line);
                }

                index++;
            }

            return result;
        }
    }

    /// <summary>Returns a snapshot copy of every retained line.</summary>
    public IReadOnlyList<StyledLine> Snapshot()
    {
        lock (_gate)
        {
            return _lines.ToArray();
        }
    }

    /// <summary>Removes all lines.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
        }
    }

    private void AppendLocked(StyledLine line)
    {
        _lines.AddLast(line);
        while (_lines.Count > Capacity)
        {
            _lines.RemoveFirst();
        }
    }
}
