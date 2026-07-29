using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SharpMUTerm.Core.Text;

/// <summary>Event payload describing lines appended to a <see cref="ScrollbackBuffer"/>.</summary>
public sealed class LinesAppendedEventArgs(IReadOnlyList<StyledLine> lines) : EventArgs
{
    public IReadOnlyList<StyledLine> Lines { get; } = lines;
}

/// <summary>
/// A thread-safe store of terminal output lines: a bounded in-memory ring of the most recent
/// <see cref="Capacity"/> lines, optionally backed by an <see cref="IScrollbackSpill"/> holding the
/// ones evicted behind it. UI-agnostic: the view subscribes to <see cref="LinesAppended"/> and pages
/// through history with <see cref="GetRange"/>, which serves memory and disk identically.
/// <para>
/// <b>Indices are absolute.</b> Line 0 is the first line this buffer ever saw, and an index never
/// comes to mean a different line, so a scroll position stays meaningful while output keeps
/// arriving. <see cref="TotalLines"/> is one past the newest; <see cref="OldestIndex"/> is the oldest
/// still retrievable and advances as history is reclaimed. <see cref="GetRange"/> clamps to that
/// window rather than throwing, so a stale scroll position returns fewer lines instead of failing.
/// </para>
/// <para>
/// <b>Thread safety.</b> Every member is safe to call from any thread. Appends (the telnet read
/// loop) and reads (the UI thread) are serialised on one lock, and the spill's I/O happens under it —
/// so a read never observes a line half-way between memory and disk.
/// <see cref="LinesAppended"/> is raised outside the lock, on the appending thread.
/// </para>
/// </summary>
public sealed class ScrollbackBuffer : IDisposable
{
    /// <summary>
    /// The most lines <see cref="GetRange"/> will return in one call. Deliberately far smaller than
    /// the history a buffer can hold: a windowed view asks for the rows it is about to draw, and a
    /// caller that wants everything must page for it rather than materialising 100k lines by
    /// accident — which is the cost this whole mechanism exists to avoid.
    /// </summary>
    public const int MaxRangeLines = 4096;

    /// <summary>
    /// Lines retained per window unless a caller (or <c>AppConfiguration.ScrollbackLines</c>) says
    /// otherwise. The single definition, so the config default, the session default and this buffer's
    /// own default cannot drift apart.
    /// <para>
    /// Ten thousand, not the twenty thousand it was. The number is not free: the TUI feeds a window's
    /// whole buffer to one markup control whose parse cache is keyed on a content version, and
    /// appending a line bumps that version — so <em>one</em> arriving line re-parses the entire buffer.
    /// Measured against SharpConsoleUI 2.5.14 that is ~11 ms at 1,000 lines, ~50 ms at 5,000 and
    /// 88–116 ms at 20,000: at twenty thousand a busy room drops the client below a frame a second.
    /// Halving the cap is a mitigation, not the fix — a windowed feed (giving the control the visible
    /// slice instead of everything) is what removes the coupling — but a default nobody can survive is
    /// not one to keep in the meantime, and ten thousand lines is still hours of a room's traffic.
    /// </para>
    /// <para>
    /// Deeper history is not lost when a <see cref="IScrollbackSpill"/> is supplied — it is paged off
    /// disk instead, so this cap governs what stays in <em>memory</em> (and therefore what the markup
    /// control can be asked to hold), not how far back the user can scroll.
    /// </para>
    /// </summary>
    public const int DefaultCapacity = 10_000;

    private readonly object _gate = new();
    private readonly IScrollbackSpill? _spill;

    /// <summary>The in-memory ring, grown geometrically up to <see cref="Capacity"/>.</summary>
    private StyledLine[] _ring = Array.Empty<StyledLine>();

    private int _head;
    private int _count;
    private long _memoryStart;
    private long _total;
    private ILogger _logger = NullLogger.Instance;

    /// <param name="capacity">How many lines stay in memory.</param>
    /// <param name="spill">
    /// Where evicted lines go. Null (the default) means memory-only: nothing is written anywhere and
    /// history beyond <paramref name="capacity"/> is lost. That is the right default for a Core
    /// primitive — the session decides whether this window's history is worth caching to disk.
    /// </param>
    public ScrollbackBuffer(int capacity = DefaultCapacity, IScrollbackSpill? spill = null)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be at least 1.");
        }

        Capacity = capacity;
        _spill = spill;
    }

    /// <summary>Maximum number of lines held in memory. Older lines live in the spill, if there is one.</summary>
    public int Capacity { get; }

    /// <summary>Raised after one or more lines are appended.</summary>
    public event EventHandler<LinesAppendedEventArgs>? LinesAppended;

    /// <summary>
    /// Where this buffer's — and its spill's — diagnostics go. Defaults to
    /// <see cref="NullLogger.Instance"/> so Core stays free of any logging implementation; the app
    /// sets it (through <c>WorldSession.Logger</c>) to its client diagnostics pipeline.
    /// </summary>
    public ILogger Logger
    {
        get => _logger;
        set
        {
            _logger = value ?? NullLogger.Instance;
            if (_spill is not null)
            {
                _spill.Logger = _logger;
            }
        }
    }

    /// <summary>How many lines are held in memory (at most <see cref="Capacity"/>).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>
    /// One past the absolute index of the newest line — every line this buffer has ever been given,
    /// including those now on disk or dropped. Only <see cref="Clear"/> resets it.
    /// </summary>
    public long TotalLines
    {
        get
        {
            lock (_gate)
            {
                return _total;
            }
        }
    }

    /// <summary>The absolute index of the oldest line <see cref="GetRange"/> can still return.</summary>
    public long OldestIndex
    {
        get
        {
            lock (_gate)
            {
                return OldestIndexLocked;
            }
        }
    }

    /// <summary>How deep the retrievable history is right now: memory plus spill.</summary>
    public long AvailableLines
    {
        get
        {
            lock (_gate)
            {
                return _total - OldestIndexLocked;
            }
        }
    }

    /// <summary>How many retrievable lines are held by the spill rather than in memory.</summary>
    public long SpilledLines
    {
        get
        {
            lock (_gate)
            {
                return _memoryStart - OldestIndexLocked;
            }
        }
    }

    /// <summary>
    /// Whether a spill is attached and still working. False for a memory-only buffer, and for one
    /// whose spill hit a disk fault and degraded (see <see cref="IScrollbackSpill"/>).
    /// </summary>
    public bool IsSpilling => _spill is { IsHealthy: true };

    /// <summary>Appends a single line, spilling the oldest to disk once the memory window is full.</summary>
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

    /// <summary>
    /// Returns the lines at absolute indices <c>[start, start + count)</c>, drawn from memory, from
    /// the spill, or from both — the caller does not need to know which. Indices outside
    /// <c>[OldestIndex, TotalLines)</c> are clamped away.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="count"/> exceeds <see cref="MaxRangeLines"/>; page instead.
    /// </exception>
    public IReadOnlyList<StyledLine> GetRange(long start, int count)
    {
        if (start < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        ValidateCount(count);

        lock (_gate)
        {
            return GetRangeLocked(start, count);
        }
    }

    /// <summary>
    /// Returns the newest <paramref name="count"/> lines — what a view wants when it is scrolled to
    /// the bottom. Taken under one lock, so it cannot straddle a concurrent append.
    /// </summary>
    public IReadOnlyList<StyledLine> GetTail(int count)
    {
        ValidateCount(count);

        lock (_gate)
        {
            return GetRangeLocked(Math.Max(0, _total - count), count);
        }
    }

    /// <summary>
    /// Returns a snapshot copy of the <em>in-memory</em> window only — bounded by
    /// <see cref="Capacity"/> by construction. Use <see cref="GetRange"/> to reach spilled history.
    /// </summary>
    public IReadOnlyList<StyledLine> Snapshot()
    {
        lock (_gate)
        {
            var result = new StyledLine[_count];
            for (var i = 0; i < _count; i++)
            {
                result[i] = _ring[RingIndex(i)];
            }

            return result;
        }
    }

    /// <summary>Removes every line, in memory and on disk. Absolute indices restart at zero.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _head = 0;
            _count = 0;
            _memoryStart = 0;
            _total = 0;
            _spill?.Clear();
        }
    }

    /// <summary>Releases the spill, deleting its cache. The in-memory window is unaffected.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _spill?.Dispose();
        }
    }

    private static void ValidateCount(int count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (count > MaxRangeLines)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                count,
                $"A single range is limited to {MaxRangeLines} lines; request history in pages.");
        }
    }

    /// <summary>The oldest retrievable index: the spill's first line when it holds any, else the memory window's.</summary>
    private long OldestIndexLocked =>
        _spill is { IsHealthy: true } spill && spill.Count > 0 ? spill.FirstIndex : _memoryStart;

    private IReadOnlyList<StyledLine> GetRangeLocked(long start, int count)
    {
        var from = Math.Max(start, OldestIndexLocked);
        var to = Math.Min(start + count, _total);
        if (to <= from)
        {
            return Array.Empty<StyledLine>();
        }

        var result = new List<StyledLine>((int)(to - from));

        // The spilled part first, then the in-memory tail; either may be empty. The spill returns one
        // line per index it was asked for, so the two halves stay contiguous even if a record is
        // unreadable (it comes back blank).
        if (from < _memoryStart && _spill is not null)
        {
            var spillEnd = Math.Min(to, _memoryStart);
            _spill.Read(from, (int)(spillEnd - from), result);
        }

        for (var index = Math.Max(from, _memoryStart); index < to; index++)
        {
            result.Add(_ring[RingIndex((int)(index - _memoryStart))]);
        }

        return result;
    }

    private void AppendLocked(StyledLine line)
    {
        if (_count == Capacity)
        {
            var evicted = _ring[_head];
            _ring[_head] = null!;
            _head = _head + 1 == _ring.Length ? 0 : _head + 1;
            _count--;
            _memoryStart++;

            // Under the lock on purpose: the spill's indices are this buffer's, so a line has to reach
            // it before the next eviction, and a concurrent read must not see the gap in between.
            _spill?.Append(evicted);
        }
        else
        {
            GrowRingIfNeeded();
        }

        _ring[RingIndex(_count)] = line;
        _count++;
        _total++;
    }

    /// <summary>Maps an offset from the oldest in-memory line to its slot in the ring.</summary>
    private int RingIndex(int offset)
    {
        var index = _head + offset;
        return index >= _ring.Length ? index - _ring.Length : index;
    }

    /// <summary>
    /// Doubles the ring (up to <see cref="Capacity"/>) when it fills. Growing on demand rather than
    /// allocating <see cref="Capacity"/> references up front keeps a dozen idle windows cheap.
    /// </summary>
    private void GrowRingIfNeeded()
    {
        if (_count < _ring.Length)
        {
            return;
        }

        var grown = new StyledLine[Math.Min(Capacity, Math.Max(16, _ring.Length * 2))];
        for (var i = 0; i < _count; i++)
        {
            grown[i] = _ring[RingIndex(i)];
        }

        _ring = grown;
        _head = 0;
    }
}
