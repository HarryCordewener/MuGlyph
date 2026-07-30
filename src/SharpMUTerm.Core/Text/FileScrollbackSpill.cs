using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace SharpMUTerm.Core.Text;

/// <summary>
/// A segmented, file-backed <see cref="IScrollbackSpill"/>: the disk half of a
/// <see cref="ScrollbackBuffer"/>, holding the lines that no longer fit in memory so the view can
/// scroll past its in-memory window.
/// <para>
/// <b>Lifetime.</b> An ephemeral, per-session, per-buffer <em>cache</em>. Nothing is created until
/// the first line is actually evicted; everything is deleted on <see cref="Dispose"/>, and whatever
/// a crash left behind is purged the first time a store is created in a new process. It is
/// deliberately <b>not</b> the user's session log: <c>PlainTextLogSink</c>/<c>HtmlLogSink</c> are
/// opt-in, formatted, and kept — a scrollback cache that behaved like them would be writing a
/// transcript of private play to disk that nobody asked for. Set
/// <see cref="ScrollbackSpillOptions.Enabled"/> to false to write nothing at all.
/// </para>
/// <para>
/// <b>Layout.</b> Each store owns a directory under the platform cache root
/// (<see cref="DefaultRoot"/>) named for the process that owns it, holding a <c>.lock</c> file — kept
/// open with <see cref="FileShare.None"/> so a concurrent instance's purge can tell a live store from
/// an abandoned one — and a series of segment files. A segment is a 16-byte header followed by
/// length-prefixed, CRC-32-checked records (see <see cref="StyledLineCodec"/> for the payload).
/// Space is reclaimed by deleting the oldest segment whole, which is an <c>unlink</c> rather than a
/// rewrite of everything on every line.
/// </para>
/// <para>
/// <b>Seeking.</b> Every record's byte offset is held in memory (8 bytes a line — 800 KB for the
/// 100k-line default bound), so serving lines <c>i..j</c> is one <c>pread</c> of the exact byte span
/// they occupy, never a scan. The index is never rebuilt from disk because it never outlives the
/// process that wrote it.
/// </para>
/// <para>
/// <b>Failure.</b> No method throws for an I/O reason. The first failure logs once, tears the store
/// down, and leaves it <see cref="IsHealthy"/> false — the buffer is memory-only from then on and the
/// live line is unaffected. A record that fails its checksum, or a file truncated behind our back,
/// yields <see cref="StyledLine.Empty"/> for that index so a paged read stays aligned.
/// </para>
/// <para><b>Thread safety.</b> All members are safe to call concurrently; every operation is serialised on one lock.</para>
/// </summary>
public sealed class FileScrollbackSpill : IScrollbackSpill
{
    /// <summary>Magic bytes at the head of every segment file.</summary>
    private static readonly byte[] Magic = "SMTSPILL"u8.ToArray();

    /// <summary>Bumped if the record or payload layout ever changes. Segments never outlive a process, so this is a sanity check, not a migration.</summary>
    private const uint FormatVersion = 1;

    private const int HeaderBytes = 16;
    private const int LengthPrefixBytes = sizeof(uint);
    private const int ChecksumBytes = sizeof(uint);
    private const int FrameOverheadBytes = LengthPrefixBytes + ChecksumBytes;

    /// <summary>Appends are staged here and written in one <c>pwrite</c> per bufferful.</summary>
    private const int WriteBufferBytes = 64 * 1024;

    /// <summary>Largest payload we will store. A line bigger than this is spilled as a blank row rather than dropped, so indices stay aligned.</summary>
    private const int MaxRecordBytes = 16 * 1024 * 1024;

    /// <summary>Upper bound on one <c>pread</c> while serving a range; a longer run is read in several.</summary>
    private const int ReadChunkBytes = 1024 * 1024;

    /// <summary>The encode scratch buffer is released back to this size once a huge line has grown it.</summary>
    private const int ScratchTrimBytes = 1024 * 1024;

    private const string StoreDirectoryPrefix = "store-";
    private const string LockFileName = ".lock";
    private const string SegmentPattern = "seg-*.bin";

    private static readonly HashSet<string> PurgedRoots = new(StringComparer.Ordinal);
    private static int _storeSequence;

    private readonly object _gate = new();
    private readonly List<Segment> _segments = new();
    private readonly MemoryStream _scratch = new(256);
    private readonly BinaryWriter _scratchWriter;
    private readonly byte[] _writeBuffer = new byte[WriteBufferBytes];
    private readonly string _root;
    private readonly string? _label;
    private readonly long _maxBytes;
    private readonly long _maxLines;
    private readonly long _segmentBytes;

    private string? _directory;
    private FileStream? _lockFile;
    private int _segmentSequence;
    private int _buffered;
    private long _first;
    private long _end;
    private long _totalBytes;
    private bool _healthy = true;
    private bool _faultLogged;
    private bool _corruptionLogged;
    private bool _oversizeLogged;
    private bool _disposed;

    /// <summary>
    /// Creates a store for one buffer. Nothing touches the disk here — the directory, lock, and first
    /// segment are created when the first line is evicted, so a session that never fills its
    /// in-memory window leaves no trace.
    /// </summary>
    /// <param name="options">Bounds and location; null means the defaults.</param>
    /// <param name="label">
    /// An optional hint folded into the directory name (a session key, say) so a store is
    /// identifiable while it exists. Sanitised; never load-bearing for uniqueness.
    /// </param>
    public FileScrollbackSpill(ScrollbackSpillOptions? options = null, string? label = null)
    {
        options ??= new ScrollbackSpillOptions();
        _root = string.IsNullOrWhiteSpace(options.Directory) ? DefaultRoot : options.Directory!;
        _label = Sanitise(label);
        _maxLines = Math.Max(1, options.MaxLines);
        _maxBytes = Math.Max(1, (long)options.MaxMegabytes) * 1024 * 1024;

        // A segment larger than the whole budget would make the "keep at least the active segment"
        // floor bigger than the bound the user asked for.
        _segmentBytes = Math.Clamp((long)Math.Max(1, options.SegmentMegabytes) * 1024 * 1024, 64 * 1024, _maxBytes);
        _scratchWriter = new BinaryWriter(_scratch, StyledLineCodec.TextEncoding, leaveOpen: true);
    }

    /// <summary>
    /// The platform's per-user cache directory for scrollback spills: <c>$XDG_CACHE_HOME</c> (or
    /// <c>~/.cache</c>) on Linux, <c>%LOCALAPPDATA%</c> on Windows, <c>~/Library/Caches</c> on macOS.
    /// A cache location, not a config or data one — the contents are disposable by construction and
    /// nothing here should ever be backed up or synced.
    /// </summary>
    public static string DefaultRoot
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(local))
                {
                    local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
                }

                return Path.Combine(local, "SharpMUTerm", "cache", "scrollback");
            }

            // XDG_CACHE_HOME wins wherever it is set, including macOS: a user who set it meant it.
            var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            if (!string.IsNullOrWhiteSpace(xdg))
            {
                return Path.Combine(xdg, "SharpMUTerm", "scrollback");
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return OperatingSystem.IsMacOS()
                ? Path.Combine(home, "Library", "Caches", "SharpMUTerm", "scrollback")
                : Path.Combine(home, ".cache", "SharpMUTerm", "scrollback");
        }
    }

    /// <inheritdoc />
    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <inheritdoc />
    public long FirstIndex
    {
        get
        {
            lock (_gate)
            {
                return _first;
            }
        }
    }

    /// <inheritdoc />
    public long EndIndex
    {
        get
        {
            lock (_gate)
            {
                return _end;
            }
        }
    }

    /// <inheritdoc />
    public long Count
    {
        get
        {
            lock (_gate)
            {
                return _end - _first;
            }
        }
    }

    /// <inheritdoc />
    public bool IsHealthy
    {
        get
        {
            lock (_gate)
            {
                return _healthy && !_disposed;
            }
        }
    }

    /// <summary>The store's directory, or null while nothing has been spilled yet (or after a fault).</summary>
    public string? CacheDirectory
    {
        get
        {
            lock (_gate)
            {
                return _directory;
            }
        }
    }

    /// <summary>Bytes currently occupied by segment files, including anything still in the write buffer.</summary>
    public long ByteCount
    {
        get
        {
            lock (_gate)
            {
                return _totalBytes;
            }
        }
    }

    /// <summary>
    /// Deletes spill directories under <paramref name="root"/> that no live process holds — what a
    /// crash or a kill leaves behind. A store still in use keeps its <c>.lock</c> file open with
    /// <see cref="FileShare.None"/>, so it cannot be acquired and is skipped. Returns how many
    /// directories were removed; never throws.
    /// </summary>
    public static int PurgeStale(string? root = null, ILogger? logger = null)
    {
        root = string.IsNullOrWhiteSpace(root) ? DefaultRoot : root;
        var removed = 0;
        try
        {
            if (!System.IO.Directory.Exists(root))
            {
                return 0;
            }

            foreach (var directory in System.IO.Directory.EnumerateDirectories(root))
            {
                if (TryPurgeStore(directory))
                {
                    removed++;
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Purging stale scrollback spills under {Root} failed", root);
        }

        if (removed > 0)
        {
            logger?.LogInformation("Removed {Count} stale scrollback spill cache(s) under {Root}", removed, root);
        }

        return removed;
    }

    /// <inheritdoc />
    public void Append(StyledLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
        {
            if (!_healthy || _disposed)
            {
                return;
            }

            try
            {
                var length = Encode(line);
                EnsureStore();
                WriteRecord(length);
                RollIfNeeded();
                EnforceBounds();
            }
            catch (Exception ex)
            {
                Fault(ex, "writing a line");
            }
        }
    }

    /// <inheritdoc />
    public void Read(long start, int count, List<StyledLine> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (count <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (!_healthy || _disposed)
            {
                return;
            }

            var index = Math.Max(start, _first);
            var end = Math.Min(start + count, _end);
            while (index < end)
            {
                // A fault inside the loop (below) leaves nothing readable; the range still has to come
                // back the length the caller's clamp implies, or a paged view's rows shift under it.
                var segment = _healthy ? FindSegment(index) : null;
                if (segment is null)
                {
                    if (_healthy)
                    {
                        NoteCorruption($"no segment holds line {index}");
                    }

                    FillEmpty(index, end, into);
                    return;
                }

                var local = (int)(index - segment.FirstIndex);
                var want = (int)Math.Min(end - index, segment.Offsets.Count - local);
                ReadFromSegment(segment, local, want, into);
                index += want;
            }
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Teardown();
            _first = 0;
            _end = 0;
            _totalBytes = 0;
            _buffered = 0;
            _segmentSequence = 0;
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
            Teardown();
            _scratchWriter.Dispose();
            _scratch.Dispose();
        }
    }

    /// <summary>Encodes a line into the scratch buffer, returning its payload length in bytes.</summary>
    private int Encode(StyledLine line)
    {
        // One enormous line would otherwise leave its buffer allocated for the rest of the session.
        if (_scratch.Capacity > ScratchTrimBytes)
        {
            _scratch.SetLength(0);
            _scratch.Capacity = WriteBufferBytes;
        }

        _scratch.SetLength(0);
        StyledLineCodec.Write(_scratchWriter, line);
        _scratchWriter.Flush();
        var length = (int)_scratch.Length;
        if (length <= MaxRecordBytes)
        {
            return length;
        }

        // Pathological single line (a server dumping megabytes without a newline). Store a blank row
        // in its place: dropping it outright would shift every later index by one.
        if (!_oversizeLogged)
        {
            _oversizeLogged = true;
            Logger.LogWarning(
                "A scrollback line of {Bytes} bytes exceeds the {Limit}-byte spill record limit; it is cached as a blank line",
                length,
                MaxRecordBytes);
        }

        _scratch.SetLength(0);
        StyledLineCodec.Write(_scratchWriter, StyledLine.Empty);
        _scratchWriter.Flush();
        return (int)_scratch.Length;
    }

    /// <summary>Frames the scratch payload (length, payload, CRC-32) and stages or writes it.</summary>
    private void WriteRecord(int length)
    {
        var segment = _segments[^1];
        var payload = _scratch.GetBuffer().AsSpan(0, length);
        var frame = length + FrameOverheadBytes;

        if (_buffered + frame > _writeBuffer.Length)
        {
            FlushBuffer(segment);
        }

        var offset = segment.Length;
        if (frame > _writeBuffer.Length)
        {
            var rented = ArrayPool<byte>.Shared.Rent(frame);
            try
            {
                Frame(rented.AsSpan(0, frame), payload);
                RandomAccess.Write(segment.Handle, rented.AsSpan(0, frame), offset);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        else
        {
            Frame(_writeBuffer.AsSpan(_buffered, frame), payload);
            _buffered += frame;
        }

        segment.Offsets.Add(offset);
        segment.Length += frame;
        _totalBytes += frame;
        _end++;
    }

    private static void Frame(Span<byte> destination, ReadOnlySpan<byte> payload)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)payload.Length);
        payload.CopyTo(destination[LengthPrefixBytes..]);
        BinaryPrimitives.WriteUInt32LittleEndian(
            destination[(LengthPrefixBytes + payload.Length)..],
            Crc32.Compute(payload));
    }

    private void FlushBuffer(Segment segment)
    {
        if (_buffered == 0)
        {
            return;
        }

        RandomAccess.Write(segment.Handle, _writeBuffer.AsSpan(0, _buffered), segment.Length - _buffered);
        _buffered = 0;
    }

    /// <summary>Creates the store directory, its lock file, and the first segment on first use.</summary>
    private void EnsureStore()
    {
        if (_segments.Count > 0)
        {
            return;
        }

        PurgeRootOnce();
        System.IO.Directory.CreateDirectory(_root);

        var name = $"{StoreDirectoryPrefix}{Environment.ProcessId}-{Interlocked.Increment(ref _storeSequence)}-{Guid.NewGuid():N}";
        if (_label is not null)
        {
            name = $"{name}-{_label}";
        }

        var directory = Path.Combine(_root, name);
        System.IO.Directory.CreateDirectory(directory);

        // Held for the store's whole life, and unlinked on close: another instance's PurgeStale can
        // then tell "in use" (cannot acquire) from "abandoned by a crash" (acquires immediately).
        _lockFile = new FileStream(
            Path.Combine(directory, LockFileName),
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.DeleteOnClose);

        _directory = directory;
        StartSegment();
    }

    private void StartSegment()
    {
        var path = Path.Combine(_directory!, $"seg-{_segmentSequence++:D6}.bin");
        var handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        var header = new byte[HeaderBytes];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(Magic.Length), FormatVersion);
        RandomAccess.Write(handle, header, 0);

        _segments.Add(new Segment(path, handle, _end) { Length = HeaderBytes });
        _totalBytes += HeaderBytes;
    }

    private void RollIfNeeded()
    {
        var segment = _segments[^1];
        if (segment.Length < _segmentBytes)
        {
            return;
        }

        FlushBuffer(segment);
        StartSegment();
    }

    /// <summary>
    /// Drops whole segments off the front until both bounds hold. The active segment is never
    /// dropped, so the floor is one segment's worth of history.
    /// </summary>
    private void EnforceBounds()
    {
        while (_segments.Count > 1 && (_totalBytes > _maxBytes || _end - _first > _maxLines))
        {
            var oldest = _segments[0];
            _segments.RemoveAt(0);
            _totalBytes -= oldest.Length;
            _first = _segments[0].FirstIndex;
            oldest.Handle.Dispose();
            TryDelete(oldest.Path);
        }
    }

    private Segment? FindSegment(long index)
    {
        // Segments are index-ordered and few (the byte bound over the segment size), so a linear
        // walk from the back is cheaper than a binary search and easier to be sure of.
        for (var i = _segments.Count - 1; i >= 0; i--)
        {
            var segment = _segments[i];
            if (index >= segment.FirstIndex && index < segment.FirstIndex + segment.Offsets.Count)
            {
                return segment;
            }
        }

        return null;
    }

    /// <summary>
    /// Adds exactly <paramref name="want"/> lines starting at local record <paramref name="local"/>,
    /// reading the byte span they occupy in as few <c>pread</c>s as <see cref="ReadChunkBytes"/> allows.
    /// </summary>
    private void ReadFromSegment(Segment segment, int local, int want, List<StyledLine> into)
    {
        if (ReferenceEquals(segment, _segments[^1]) && _buffered > 0)
        {
            // The tail of the active segment may still be in the write buffer; a reader must see it.
            try
            {
                FlushBuffer(segment);
            }
            catch (Exception ex)
            {
                Fault(ex, "flushing before a read");
                FillEmpty(0, want, into);
                return;
            }
        }

        if (!HeaderIsValid(segment))
        {
            FillEmpty(0, want, into);
            return;
        }

        var done = 0;
        while (done < want)
        {
            var first = local + done;
            var spanStart = segment.Offsets[first];
            var chunk = 0;
            var spanEnd = spanStart;
            while (first + chunk < local + want)
            {
                var recordEnd = RecordEnd(segment, first + chunk);
                if (chunk > 0 && recordEnd - spanStart > ReadChunkBytes)
                {
                    break;
                }

                spanEnd = recordEnd;
                chunk++;
            }

            var spanLength = (int)(spanEnd - spanStart);
            var buffer = ArrayPool<byte>.Shared.Rent(spanLength);
            try
            {
                var got = ReadAt(segment.Handle, buffer.AsSpan(0, spanLength), spanStart);
                DecodeRecords(segment, first, chunk, spanStart, buffer, got, into);
            }
            catch (Exception ex)
            {
                // A read failing outright (handle closed, file removed) is a fault, not corruption.
                // Top the result up to `want` so the caller's range keeps its length.
                Fault(ex, "reading spilled lines");
                FillEmpty(done, want, into);
                return;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            done += chunk;
        }
    }

    private static void FillEmpty(long from, long to, List<StyledLine> into)
    {
        for (var i = from; i < to; i++)
        {
            into.Add(StyledLine.Empty);
        }
    }

    /// <summary>
    /// Checks a segment's magic and format version before reading records out of it. One 16-byte
    /// <c>pread</c> per segment per range, which is noise next to the range itself, and it catches a
    /// file replaced or overwritten wholesale behind our back — something per-record checksums would
    /// only notice one record at a time.
    /// </summary>
    private bool HeaderIsValid(Segment segment)
    {
        Span<byte> header = stackalloc byte[HeaderBytes];
        if (ReadAt(segment.Handle, header, 0) != HeaderBytes
            || !header[..Magic.Length].SequenceEqual(Magic)
            || BinaryPrimitives.ReadUInt32LittleEndian(header[Magic.Length..]) != FormatVersion)
        {
            NoteCorruption($"{segment.Path} is not a spill segment written by this session");
            return false;
        }

        return true;
    }

    private long RecordEnd(Segment segment, int local) =>
        local + 1 < segment.Offsets.Count ? segment.Offsets[local + 1] : segment.Length;

    /// <summary>
    /// Validates and decodes <paramref name="chunk"/> records out of a byte span read from disk. A
    /// record that is short (the file was truncated behind us), self-inconsistent, or fails its
    /// CRC-32 becomes <see cref="StyledLine.Empty"/> — detected, logged once, and never returned as
    /// garbage.
    /// </summary>
    private void DecodeRecords(
        Segment segment,
        int local,
        int chunk,
        long spanStart,
        byte[] buffer,
        int got,
        List<StyledLine> into)
    {
        // One reader for the whole chunk, positioned per record, rather than one per line: a 40-row
        // page then costs two allocations instead of eighty.
        using var stream = new MemoryStream(buffer, 0, got, writable: false);
        using var reader = new BinaryReader(stream, StyledLineCodec.TextEncoding, leaveOpen: true);

        for (var i = 0; i < chunk; i++)
        {
            var offset = (int)(segment.Offsets[local + i] - spanStart);
            var available = got - offset;
            if (available < FrameOverheadBytes)
            {
                NoteCorruption($"{segment.Path} is shorter than its index expects");
                into.Add(StyledLine.Empty);
                continue;
            }

            var length = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));
            if (length > MaxRecordBytes || length + (long)FrameOverheadBytes > available)
            {
                NoteCorruption($"record {local + i} in {segment.Path} declares {length} bytes but {available} are readable");
                into.Add(StyledLine.Empty);
                continue;
            }

            var payloadStart = offset + LengthPrefixBytes;
            var payload = buffer.AsSpan(payloadStart, (int)length);
            var expected = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(payloadStart + (int)length));
            if (Crc32.Compute(payload) != expected)
            {
                NoteCorruption($"record {local + i} in {segment.Path} failed its checksum");
                into.Add(StyledLine.Empty);
                continue;
            }

            try
            {
                stream.Position = payloadStart;
                into.Add(StyledLineCodec.Read(reader));
            }
            catch (Exception ex)
            {
                // Checksum-clean but undecodable means the format itself disagrees with the reader.
                NoteCorruption($"record {local + i} in {segment.Path} could not be decoded: {ex.Message}");
                into.Add(StyledLine.Empty);
            }
        }
    }

    /// <summary>Fills <paramref name="destination"/> from <paramref name="offset"/>, returning how many bytes were actually available.</summary>
    private static int ReadAt(SafeFileHandle handle, Span<byte> destination, long offset)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = RandomAccess.Read(handle, destination[total..], offset + total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private void NoteCorruption(string reason)
    {
        if (_corruptionLogged)
        {
            return;
        }

        _corruptionLogged = true;
        Logger.LogWarning("Scrollback spill cache is damaged ({Reason}); affected lines read back blank", reason);
    }

    /// <summary>
    /// Gives up on the disk for the rest of the session: log once, delete what we can, and become a
    /// no-op holding nothing. The caller's line is already in memory, so nothing is lost but depth.
    /// </summary>
    private void Fault(Exception ex, string what)
    {
        if (!_healthy)
        {
            return;
        }

        _healthy = false;
        if (!_faultLogged)
        {
            _faultLogged = true;
            Logger.LogWarning(
                ex,
                "Scrollback spill cache disabled for this session ({What} failed under {Root}); scrolling is limited to the in-memory window",
                what,
                _directory ?? _root);
        }

        Teardown();
        _first = 0;
        _end = 0;
        _totalBytes = 0;
        _buffered = 0;
    }

    /// <summary>Closes every handle and removes the store's directory. Swallows everything — it runs on the failure path.</summary>
    private void Teardown()
    {
        foreach (var segment in _segments)
        {
            try
            {
                segment.Handle.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Closing spill segment {Path} failed", segment.Path);
            }
        }

        _segments.Clear();
        _buffered = 0;

        try
        {
            _lockFile?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Releasing the spill lock file failed");
        }

        _lockFile = null;

        var directory = _directory;
        _directory = null;
        if (directory is null)
        {
            return;
        }

        try
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Removing the spill directory {Directory} failed", directory);
        }
    }

    private void PurgeRootOnce()
    {
        lock (PurgedRoots)
        {
            if (!PurgedRoots.Add(_root))
            {
                return;
            }
        }

        PurgeStale(_root, Logger);
    }

    /// <summary>
    /// Removes one abandoned store directory. Refuses anything that does not look like ours — the
    /// root is configurable, and a stale-cache sweep must not be able to delete a user's directory
    /// because they pointed the setting at the wrong place.
    /// </summary>
    private static bool TryPurgeStore(string directory)
    {
        try
        {
            var name = Path.GetFileName(directory);
            if (!name.StartsWith(StoreDirectoryPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            var lockPath = Path.Combine(directory, LockFileName);
            var looksLikeOurs = File.Exists(lockPath)
                || System.IO.Directory.EnumerateFiles(directory, SegmentPattern).Any();
            if (!looksLikeOurs)
            {
                return false;
            }

            if (File.Exists(lockPath))
            {
                // Throws when a live store still holds it, which is exactly the signal we want.
                using var held = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }

            System.IO.Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception)
        {
            // In use, or not ours to remove. Either way, leave it.
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // A segment we cannot unlink is not worth failing the session over; the directory goes on dispose.
        }
    }

    /// <summary>Reduces a label to something safe for a directory name, or null if nothing is left.</summary>
    private static string? Sanitise(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var chars = label.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').Take(48).ToArray();
        return chars.Length == 0 ? null : new string(chars);
    }

    /// <summary>One spill segment: a file, its read/write handle, and the byte offset of every record in it.</summary>
    private sealed class Segment(string path, SafeFileHandle handle, long firstIndex)
    {
        public string Path { get; } = path;

        public SafeFileHandle Handle { get; } = handle;

        /// <summary>Absolute index of this segment's first record.</summary>
        public long FirstIndex { get; } = firstIndex;

        /// <summary>Byte offset of each record, in index order.</summary>
        public List<long> Offsets { get; } = new();

        /// <summary>Logical length in bytes, including anything still staged in the write buffer.</summary>
        public long Length { get; set; }
    }
}
