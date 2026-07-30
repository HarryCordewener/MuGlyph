using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SharpMUTerm.Core.Text;

/// <summary>One line read back out of a <see cref="RestoreLog"/>: what it said, and when it arrived.</summary>
/// <param name="Line">The styled line, exactly as it was written to the pane.</param>
/// <param name="Stamp">
/// The arrival time the pane recorded, or null when the line carried none. It is the <em>rendered</em>
/// stamp rather than a timestamp, for the same reason the live buffer holds one: the timestamp column
/// is a render-time decision, and a restored line has to be able to answer it the same way a live one
/// does.
/// </param>
public sealed record RestoredLine(StyledLine Line, string? Stamp);

/// <summary>One window's worth of restored content.</summary>
/// <param name="WindowId">The window id the lines were logged under — the key the whole design turns on.</param>
/// <param name="Title">What that window called itself when the log was created. Diagnostic only.</param>
/// <param name="LastWritten">When the file was last touched, i.e. roughly when the previous session ended.</param>
/// <param name="Lines">The readable lines, oldest first, at most <see cref="RestoreLogOptions.MaxLinesPerWindow"/>.</param>
/// <param name="Lost">
/// How many bytes of the file could not be turned into lines — a record that failed its checksum, plus
/// whatever torn remainder a crash left at the end. Zero on a clean log. Reported rather than thrown:
/// see the class remarks.
/// </param>
public sealed record RestoredWindow(
    string WindowId,
    string Title,
    DateTimeOffset LastWritten,
    IReadOnlyList<RestoredLine> Lines,
    long Lost);

/// <summary>
/// The kept, per-window record of recent pane content, so that starting the client back up refills the
/// panes the content came from instead of showing every one of them empty.
/// <para>
/// <b>Keyed by window, not by session.</b> That is the whole design. A character's own transcript is
/// already in <c>WorldSession.Scrollback</c>, but a <em>spawn</em> window's content never goes there —
/// <c>WorldSession.ProcessOutputLine</c> raises <c>SpawnLine</c> and the shell appends the result
/// straight into that window's markup buffer — so restoring session scrollback would bring the main
/// windows back and leave every channel pane blank, which is precisely the failure this exists to
/// avoid. Logging per window covers both kinds with one mechanism, and it agrees with
/// <c>AppConfiguration.LastSession</c>, which persists window ids and where their panes sit. The two
/// are joined loosely on purpose: a window id in the log that the saved workspace no longer knows about
/// is buffered anyway (so the pane refills if that window is ever opened again), and a window in the
/// saved workspace with no log simply starts empty. Neither is an error.
/// </para>
/// <para>
/// <b>Not the spill, and not a transcript.</b> <see cref="FileScrollbackSpill"/> is an ephemeral cache
/// that purges itself on the next launch — most of a restore log and deliberately not one.
/// <c>PlainTextLogSink</c>/<c>HtmlLogSink</c> are opt-in transcripts a user keeps, formats and reads.
/// This is a third thing: a small, bounded, machine-readable tail of each pane, on by default, whose
/// only consumer is the client's own startup. It is plaintext at rest, in the configuration directory,
/// owner-only (<c>0600</c>) like <c>secrets.json</c>; a character opts out on F9 and ⌃P purges the lot.
/// </para>
/// <para>
/// <b>Layout.</b> One file per window under the root, named for a sanitised form of the window id plus a
/// checksum of it (the id itself may hold characters a filename may not, and two ids must never collide
/// on one file). Each file is a variable-length header — magic, format version, and the window's id and
/// title, checksummed — followed by <c>length · payload · CRC-32</c> records whose payload is a
/// <see cref="StyledLineCodec"/> line with its arrival stamp. Reusing the codec is not only economy: it
/// is the one format in this codebase that can carry a palette index, a span's
/// <see cref="SpanInteraction"/> and a line's rule colour without lying about any of them.
/// </para>
/// <para>
/// <b>Append as you go, not flush on exit.</b> Every line is written and flushed to the operating system
/// as it arrives, so a crash — an unhandled exception, an OOM kill, a <c>kill -9</c>, a closed terminal
/// — loses nothing at all: the bytes are already the kernel's. A restore log that only worked when you
/// quit politely would fail in exactly the case that motivates it. What is deliberately <em>not</em>
/// done is an <c>fsync</c> per line: that is a device round-trip for every line a busy game sends, and
/// the only failure it would additionally cover is losing mains power mid-session, where losing the last
/// few lines of chat is an acceptable outcome. A periodic flush was the other candidate and is strictly
/// worse — it trades the same syscall cost for a window of seconds in which a crash loses output, and
/// interesting output is what tends to precede a crash.
/// </para>
/// <para>
/// <b>Bounded in lines.</b> Each file holds at most twice <see cref="RestoreLogOptions.MaxLinesPerWindow"/>
/// records and is compacted back down to the bound — a contiguous copy of the newest records into a
/// temporary file, then an atomic rename — so the amortised cost is one extra record written per record
/// appended, and space is reclaimed without ever rewriting the file per line. The bound is in lines and
/// must stay in lines; see <see cref="RestoreLogOptions.MaxLinesPerWindow"/>.
/// </para>
/// <para>
/// <b>Nothing here throws at a caller.</b> Not on a missing directory, a permission error, a truncated
/// file, a bad checksum, or a header from a future version. A read <em>skips</em> a record that fails
/// its checksum and carries on, and <em>stops</em> only where the framing itself stops making sense —
/// so a crash's torn tail costs the newest few lines rather than the file, and one damaged record costs
/// one line rather than everything after it. Either way the lost byte count comes back for the caller
/// to log. A write that fails takes that one window's file out of service for the session and leaves
/// the pane itself untouched. Startup is the caller here, and a client that refuses to start because of
/// a cache of old chat lines would be a worse client than one that starts with an empty pane.
/// </para>
/// <para><b>Thread safety.</b> All members are safe to call concurrently; every operation is serialised on one lock.</para>
/// </summary>
public sealed class RestoreLog : IDisposable
{
    /// <summary>Magic bytes at the head of every window log.</summary>
    private static readonly byte[] Magic = "SMTRSTOR"u8.ToArray();

    /// <summary>
    /// Bumped if the record or header layout ever changes. Unlike the spill's, these files <em>do</em>
    /// outlive the process that wrote them, so this is a real gate: a file whose version this build does
    /// not know is left alone and read as nothing rather than reinterpreted.
    /// </summary>
    private const uint FormatVersion = 1;

    private const int MagicBytes = 8;
    private const int FixedHeaderBytes = MagicBytes + (3 * sizeof(uint)); // magic, version, payload length, payload CRC
    private const int LengthPrefixBytes = sizeof(uint);
    private const int ChecksumBytes = sizeof(uint);
    private const int FrameOverheadBytes = LengthPrefixBytes + ChecksumBytes;

    /// <summary>Largest record we will write or trust. A line beyond this is logged as a blank one.</summary>
    private const int MaxRecordBytes = 1024 * 1024;

    /// <summary>The extension every window log carries — chosen so <c>.gitignore</c>'s <c>*.log</c> already covers it.</summary>
    internal const string FileExtension = ".log";

    /// <summary>Where a compaction stages the new file before the rename that replaces the old one.</summary>
    private const string TempExtension = ".tmp";

    /// <summary>
    /// The mode these files are written with on Unix: <c>0600</c>, the same as <c>secrets.json</c>. They
    /// hold what was said in a private game, which is nobody else's business on a shared machine.
    /// </summary>
    /// <remarks>
    /// Spelt out here rather than taken from <c>SecretsStore.OwnerOnlyMode</c> so that
    /// <c>SharpMUTerm.Core.Text</c> keeps pointing at nothing in <c>SharpMUTerm.Core.Configuration</c> —
    /// the dependency already runs the other way (<c>AppConfiguration</c> holds a
    /// <see cref="RestoreLogOptions"/>).
    /// </remarks>
    private const UnixFileMode OwnerOnlyFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>And the directory holding them, <c>0700</c> — a file name here is a channel's name.</summary>
    private const UnixFileMode OwnerOnlyDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly object _gate = new();
    private readonly Dictionary<string, Writer> _writers = new(StringComparer.Ordinal);
    private readonly MemoryStream _scratch = new(256);
    private readonly BinaryWriter _scratchWriter;
    private readonly int _maxLines;
    private bool _disposed;
    private bool _faultLogged;

    /// <summary>Creates a log over <paramref name="root"/>. Nothing touches the disk until a line is appended or read.</summary>
    /// <param name="root">The directory holding the per-window files. Created on first write.</param>
    /// <param name="options">Bounds; null means the defaults.</param>
    public RestoreLog(string root, RestoreLogOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = root;
        _maxLines = Math.Max(1, (options ?? new RestoreLogOptions()).MaxLinesPerWindow);
        _scratchWriter = new BinaryWriter(_scratch, StyledLineCodec.TextEncoding, leaveOpen: true);
    }

    /// <summary>The directory holding the per-window files.</summary>
    public string Root { get; }

    /// <summary>Where the logs go for a given configuration file: a <c>restore</c> directory beside it.</summary>
    public static string DefaultRoot(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(configurationPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(configurationPath)) ?? string.Empty;
        return Path.Combine(directory, RestoreLogOptions.DirectoryName);
    }

    /// <summary>Where diagnostics go. Defaults to nowhere, so Core stays free of a logging implementation.</summary>
    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>
    /// Records one line as having been shown in <paramref name="windowId"/>. Cheap and synchronous: one
    /// encode and one buffered write, flushed to the operating system before returning.
    /// </summary>
    /// <param name="windowId">The window the line was drawn in — main window or spawn window alike.</param>
    /// <param name="title">What that window calls itself, recorded in the file's header on creation.</param>
    /// <param name="line">The line.</param>
    /// <param name="stamp">The arrival stamp the pane recorded, or null.</param>
    public void Append(string windowId, string title, StyledLine line, string? stamp)
    {
        ArgumentException.ThrowIfNullOrEmpty(windowId);
        ArgumentNullException.ThrowIfNull(line);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var writer = WriterFor(windowId, title ?? string.Empty);
            if (writer is null)
            {
                return;
            }

            try
            {
                var payload = EncodeRecord(line, stamp);
                writer.Append(payload, _maxLines);
            }
            catch (Exception ex)
            {
                Fault(ex, windowId);
            }
        }
    }

    /// <summary>
    /// Reads every window log under the root, newest-written first. Never throws: a file that cannot be
    /// opened is skipped, and one that is damaged contributes whatever prefix of it is trustworthy with
    /// the rest counted into <see cref="RestoredWindow.Lost"/>.
    /// </summary>
    public IReadOnlyList<RestoredWindow> Read()
    {
        lock (_gate)
        {
            var restored = new List<RestoredWindow>();
            IEnumerable<string> files;
            try
            {
                if (!Directory.Exists(Root))
                {
                    return restored;
                }

                files = Directory.EnumerateFiles(Root, "*" + FileExtension).OrderBy(f => f, StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Listing the restore log under {Root} failed", Root);
                return restored;
            }

            foreach (var file in files)
            {
                if (ReadFile(file) is { } window)
                {
                    restored.Add(window);
                }
            }

            return restored
                .OrderByDescending(w => w.LastWritten)
                .ToList();
        }
    }

    /// <summary>
    /// Deletes every window log and the directory itself, and hands back how many files went. The log
    /// keeps working afterwards — this is "forget what is on disk now", not "switch the feature off" —
    /// so a pane that goes on printing starts a fresh file.
    /// </summary>
    public int Purge()
    {
        lock (_gate)
        {
            CloseWriters();

            var removed = 0;
            try
            {
                if (!Directory.Exists(Root))
                {
                    return 0;
                }

                foreach (var file in Directory.EnumerateFiles(Root, "*" + FileExtension)
                             .Concat(Directory.EnumerateFiles(Root, "*" + FileExtension + TempExtension)))
                {
                    if (TryDelete(file))
                    {
                        removed++;
                    }
                }

                // Only when we emptied it: a root the user pointed at something else keeps whatever
                // else is in there, and an empty directory is not worth a second failure mode.
                if (!Directory.EnumerateFileSystemEntries(Root).Any())
                {
                    Directory.Delete(Root);
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Purging the restore log under {Root} failed", Root);
            }

            return removed;
        }
    }

    /// <summary>
    /// Drops one window's log — what a character's opt-out does to content already on disk. A window
    /// with no file simply returns false.
    /// </summary>
    public bool Forget(string windowId)
    {
        ArgumentException.ThrowIfNullOrEmpty(windowId);
        lock (_gate)
        {
            if (_writers.Remove(windowId, out var writer))
            {
                writer.Dispose();
            }

            return TryDelete(PathFor(windowId));
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
            CloseWriters();
            _scratchWriter.Dispose();
            _scratch.Dispose();
        }
    }

    /// <summary>The file one window's lines live in: a readable stem plus a checksum of the exact id.</summary>
    /// <remarks>
    /// The stem is only for a human looking at the directory. The checksum is what makes the name
    /// <em>correct</em>: window ids carry characters a filename may not (<c>spawn:Chat</c>), and two ids
    /// that sanitise to the same stem — a channel called <c>Chat/OOC</c> and one called <c>Chat OOC</c> —
    /// must not share a file. The id itself is stored in the header, so nothing ever has to read it back
    /// out of the name.
    /// </remarks>
    internal string PathFor(string windowId)
    {
        var bytes = StyledLineCodec.TextEncoding.GetBytes(windowId);
        var stem = new string(windowId.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(40).ToArray());
        if (stem.Length == 0)
        {
            stem = "window";
        }

        return Path.Combine(Root, $"{stem}-{Crc32.Compute(bytes):x8}{FileExtension}");
    }

    /// <summary>Encodes one record's payload: the arrival stamp, then the line.</summary>
    private byte[] EncodeRecord(StyledLine line, string? stamp)
    {
        _scratch.SetLength(0);
        _scratchWriter.Write((byte)(stamp is null ? 0 : 1));
        if (stamp is not null)
        {
            _scratchWriter.Write(stamp);
        }

        StyledLineCodec.Write(_scratchWriter, line);
        _scratchWriter.Flush();

        if (_scratch.Length > MaxRecordBytes)
        {
            // A server dumping a megabyte without a newline. Keep the row (so the pane comes back the
            // right length) and drop the text, rather than refusing the write or growing the file.
            _scratch.SetLength(0);
            _scratchWriter.Write((byte)0);
            StyledLineCodec.Write(_scratchWriter, StyledLine.Empty);
            _scratchWriter.Flush();
        }

        return _scratch.ToArray();
    }

    private Writer? WriterFor(string windowId, string title)
    {
        if (_writers.TryGetValue(windowId, out var writer))
        {
            return writer;
        }

        try
        {
            EnsureRoot();
            writer = Writer.Open(PathFor(windowId), windowId, title, Logger);
            _writers[windowId] = writer;
            return writer;
        }
        catch (Exception ex)
        {
            Fault(ex, windowId);
            return null;
        }
    }

    private void EnsureRoot()
    {
        if (Directory.Exists(Root))
        {
            return;
        }

        Directory.CreateDirectory(Root);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(Root, OwnerOnlyDirectoryMode);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Logger.LogDebug(ex, "Narrowing the restore log directory {Root} to 0700 failed", Root);
            }
        }
    }

    /// <summary>
    /// Takes one window out of service for the rest of the session. The line it was asked to record is
    /// already on the screen and in the live buffer, so nothing the user can see is affected; the cost
    /// is that this window will not come back next launch.
    /// </summary>
    private void Fault(Exception ex, string windowId)
    {
        if (_writers.Remove(windowId, out var writer))
        {
            try
            {
                writer.Dispose();
            }
            catch (Exception disposeFailure)
            {
                Logger.LogDebug(disposeFailure, "Closing the restore log for {WindowId} failed", windowId);
            }
        }

        if (_faultLogged)
        {
            return;
        }

        _faultLogged = true;
        Logger.LogWarning(
            ex,
            "The restore log under {Root} could not be written for window {WindowId}; that pane will start empty next launch",
            Root,
            windowId);
    }

    private void CloseWriters()
    {
        foreach (var writer in _writers.Values)
        {
            try
            {
                writer.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Closing a restore log file failed");
            }
        }

        _writers.Clear();
    }

    private bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogDebug(ex, "Removing the restore log file {Path} failed", path);
            return false;
        }
    }

    /// <summary>
    /// Reads one file into a <see cref="RestoredWindow"/>, or null when it is not one of ours at all
    /// (wrong magic, a version this build does not know, a header that fails its own checksum).
    /// </summary>
    private RestoredWindow? ReadFile(string path)
    {
        byte[] bytes;
        DateTimeOffset written;
        try
        {
            bytes = File.ReadAllBytes(path);
            written = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Reading the restore log file {Path} failed", path);
            return null;
        }

        if (!TryReadHeader(bytes, out var windowId, out var title, out var dataStart))
        {
            Logger.LogWarning(
                "{Path} is not a restore log this build can read; the window it belongs to starts empty", path);
            return null;
        }

        var lines = new List<RestoredLine>();
        var offset = dataStart;
        long lost = 0;
        while (offset < bytes.Length)
        {
            if (!TryFrameRecord(bytes, offset, out var payloadStart, out var payloadLength))
            {
                // The framing itself no longer makes sense: a half-written record at the end of a file a
                // crash interrupted, or damage a length prefix did not survive. Everything from here on
                // is unreadable by construction, so stop and keep what came before.
                lost += bytes.Length - offset;
                break;
            }

            var frameEnd = payloadStart + payloadLength + ChecksumBytes;
            if (!ChecksumHolds(bytes, payloadStart, payloadLength))
            {
                // One damaged record. Its neighbours are still framed, so skip exactly it.
                lost += frameEnd - offset;
                offset = frameEnd;
                continue;
            }

            try
            {
                lines.Add(DecodeRecord(bytes, payloadStart, payloadLength));
            }
            catch (Exception ex)
            {
                // Checksum-clean and still undecodable: the payload disagrees with this reader rather
                // than being damaged. Same treatment — lose the line, keep the file.
                Logger.LogDebug(ex, "A restore log record in {Path} could not be decoded", path);
                lost += frameEnd - offset;
            }

            offset = frameEnd;
        }

        if (lost > 0)
        {
            Logger.LogWarning(
                "{Lost} byte(s) of the restore log for window {WindowId} could not be read — most likely a session "
                + "that did not exit cleanly. {Kept} line(s) were restored and the rest discarded",
                lost,
                windowId,
                lines.Count);
        }

        // The file may hold up to twice the bound between compactions; the caller asked for the bound.
        if (lines.Count > _maxLines)
        {
            lines.RemoveRange(0, lines.Count - _maxLines);
        }

        return new RestoredWindow(windowId, title, written, lines, lost);
    }

    private static RestoredLine DecodeRecord(byte[] bytes, int payloadStart, int payloadLength)
    {
        using var stream = new MemoryStream(bytes, payloadStart, payloadLength, writable: false);
        using var reader = new BinaryReader(stream, StyledLineCodec.TextEncoding, leaveOpen: true);
        var stamp = reader.ReadByte() != 0 ? reader.ReadString() : null;
        return new RestoredLine(StyledLineCodec.Read(reader), stamp);
    }

    /// <summary>
    /// Locates one record's payload from its length prefix, checking only that the frame is present and
    /// fits inside the file. False means the framing has run out — stop reading. Whether the record's
    /// <em>contents</em> can be trusted is <see cref="ChecksumHolds"/>'s separate question, and keeping
    /// the two apart is what lets a reader skip one damaged line without abandoning the ones after it.
    /// </summary>
    private static bool TryFrameRecord(byte[] bytes, int offset, out int payloadStart, out int payloadLength)
    {
        payloadStart = 0;
        payloadLength = 0;
        if (bytes.Length - offset < FrameOverheadBytes)
        {
            return false;
        }

        var declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        if (declared > MaxRecordBytes || declared + (long)FrameOverheadBytes > bytes.Length - offset)
        {
            return false;
        }

        payloadStart = offset + LengthPrefixBytes;
        payloadLength = (int)declared;
        return true;
    }

    /// <summary>Whether a framed record's payload still matches the CRC-32 written after it.</summary>
    private static bool ChecksumHolds(byte[] bytes, int payloadStart, int payloadLength) =>
        Crc32.Compute(bytes.AsSpan(payloadStart, payloadLength))
        == BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(payloadStart + payloadLength));

    /// <summary>Frames a payload into <c>length · payload · CRC-32</c>.</summary>
    private static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[payload.Length + FrameOverheadBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(LengthPrefixBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(
            frame.AsSpan(LengthPrefixBytes + payload.Length),
            Crc32.Compute(payload));
        return frame;
    }

    /// <summary>Builds the file header: magic, version, then a checksummed payload naming the window.</summary>
    private static byte[] BuildHeader(string windowId, string title)
    {
        using var stream = new MemoryStream(64);
        using (var writer = new BinaryWriter(stream, StyledLineCodec.TextEncoding, leaveOpen: true))
        {
            writer.Write(windowId);
            writer.Write(title);
        }

        var payload = stream.ToArray();
        var header = new byte[FixedHeaderBytes + payload.Length];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(MagicBytes), FormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(MagicBytes + sizeof(uint)), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(MagicBytes + (2 * sizeof(uint))), Crc32.Compute(payload));
        payload.CopyTo(header, FixedHeaderBytes);
        return header;
    }

    /// <summary>
    /// Reads and validates a header. Everything about it is checked before a single record is trusted,
    /// because a file replaced wholesale — restored from a backup, copied from another machine, or simply
    /// not ours — is something per-record checksums would only notice one record at a time.
    /// </summary>
    private static bool TryReadHeader(byte[] bytes, out string windowId, out string title, out int dataStart)
    {
        windowId = string.Empty;
        title = string.Empty;
        dataStart = 0;

        if (bytes.Length < FixedHeaderBytes
            || !bytes.AsSpan(0, MagicBytes).SequenceEqual(Magic)
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(MagicBytes)) != FormatVersion)
        {
            return false;
        }

        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(MagicBytes + sizeof(uint)));
        var expected = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(MagicBytes + (2 * sizeof(uint))));
        if (payloadLength > MaxRecordBytes || FixedHeaderBytes + (long)payloadLength > bytes.Length)
        {
            return false;
        }

        var payload = bytes.AsSpan(FixedHeaderBytes, (int)payloadLength);
        if (Crc32.Compute(payload) != expected)
        {
            return false;
        }

        try
        {
            using var stream = new MemoryStream(bytes, FixedHeaderBytes, (int)payloadLength, writable: false);
            using var reader = new BinaryReader(stream, StyledLineCodec.TextEncoding, leaveOpen: true);
            windowId = reader.ReadString();
            title = reader.ReadString();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            return false;
        }

        dataStart = FixedHeaderBytes + (int)payloadLength;
        return windowId.Length > 0;
    }

    /// <summary>
    /// One window's open file: the stream, where every record in it starts, and how long it is. The
    /// offsets are what make compaction a byte-range copy rather than a re-encode — records are
    /// contiguous, so "keep the newest N" is one span.
    /// </summary>
    private sealed class Writer : IDisposable
    {
        private readonly string _path;
        private readonly string _windowId;
        private readonly ILogger _logger;
        private readonly List<long> _offsets = new();
        private FileStream _stream;
        private int _dataStart;

        private Writer(string path, string windowId, FileStream stream, int dataStart, ILogger logger)
        {
            _path = path;
            _windowId = windowId;
            _stream = stream;
            _dataStart = dataStart;
            _logger = logger;
        }

        /// <summary>
        /// Opens (or creates) one window's file and indexes what is already in it, so a second session
        /// <em>continues</em> the log rather than replacing it — restarting twice in a minute must not
        /// leave a pane with two lines in it.
        /// <para>
        /// A torn tail is truncated away here, at the last well-framed record. It costs nothing (the
        /// reader had already stopped there) and it means the next append lands on a clean boundary
        /// instead of behind rubbish that would poison every record after it.
        /// </para>
        /// </summary>
        public static Writer Open(string path, string windowId, string title, ILogger logger)
        {
            var existing = ReadExisting(path, windowId, out var dataStart, out var offsets, out var usable);
            FileStream stream;

            if (existing)
            {
                // The mode is fixed up rather than set at creation: this file already has an inode, and
                // one written by an older build (or copied in) should still end up owner-only.
                RestrictToOwner(path, logger);
                stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
                if (usable < stream.Length)
                {
                    logger.LogInformation(
                        "Trimming {Bytes} unreadable byte(s) from the end of the restore log for window {WindowId}",
                        stream.Length - usable,
                        windowId);
                    stream.SetLength(usable);
                }
            }
            else
            {
                // UnixCreateMode, not a chmod afterwards: the mode is applied by the open that makes the
                // inode, so the umask never gets a chance to widen it and there is no window in which a
                // world-readable file already holds someone's chat.
                stream = new FileStream(path, CreateOwnerOnly(FileShare.Read));

                var header = BuildHeader(windowId, title);
                stream.Write(header);
                stream.Flush();
                dataStart = header.Length;
                offsets = new List<long>();
            }

            stream.Seek(0, SeekOrigin.End);
            var writer = new Writer(path, windowId, stream, dataStart, logger);
            writer._offsets.AddRange(offsets);
            return writer;
        }

        /// <summary>Appends one framed record and pushes it to the operating system; compacts when the file has doubled.</summary>
        public void Append(byte[] payload, int maxLines)
        {
            var offset = _stream.Length;
            _stream.Write(Frame(payload));

            // Flush, not FlushAsync and not Flush(true). This puts the bytes in the kernel — which is
            // what makes the log survive the client dying — without an fsync per line of chat.
            _stream.Flush();
            _offsets.Add(offset);

            if (_offsets.Count > 2 * maxLines)
            {
                Compact(maxLines);
            }
        }

        public void Dispose() => _stream.Dispose();

        /// <summary>
        /// Rewrites the file with only the newest <paramref name="maxLines"/> records, through a
        /// temporary file and an atomic rename — so a crash mid-compaction leaves either the whole old
        /// file or the whole new one, never a half of either.
        /// </summary>
        private void Compact(int maxLines)
        {
            var keepFrom = _offsets[^maxLines];
            var temporary = _path + TempExtension;

            try
            {
                _stream.Flush();
                using (var source = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var destination = new FileStream(temporary, CreateOwnerOnly(FileShare.None)))
                {
                    var header = new byte[_dataStart];
                    source.ReadExactly(header);
                    destination.Write(header);

                    source.Seek(keepFrom, SeekOrigin.Begin);
                    source.CopyTo(destination);
                }

                _stream.Dispose();
                File.Move(temporary, _path, overwrite: true);

                _stream = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.Read);
                _stream.Seek(0, SeekOrigin.End);

                var shift = keepFrom - _dataStart;
                var kept = _offsets.Skip(_offsets.Count - maxLines).Select(o => o - shift).ToList();
                _offsets.Clear();
                _offsets.AddRange(kept);
            }
            catch (Exception ex)
            {
                // Compaction failing is not worth losing the log over: the file simply keeps growing
                // until the next attempt succeeds, and the reader takes the newest `maxLines` anyway.
                _logger.LogDebug(ex, "Compacting the restore log for window {WindowId} failed", _windowId);
                TryRemove(temporary);
                if (!_stream.CanWrite)
                {
                    _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
                }
            }
        }

        private static void TryRemove(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A stray .tmp is harmless: it is not a *.log and the next compaction overwrites it.
            }
        }

        /// <summary>
        /// Indexes an existing file: its header, every well-framed record in it, and the offset the
        /// first unusable byte starts at. Framing only — the checksums are the reader's business, and a
        /// damaged record in the middle must still be *skipped over* rather than truncating everything
        /// written after it.
        /// </summary>
        private static bool ReadExisting(
            string path, string windowId, out int dataStart, out List<long> offsets, out long usable)
        {
            dataStart = 0;
            offsets = new List<long>();
            usable = 0;

            byte[] bytes;
            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            if (!TryReadHeader(bytes, out var storedId, out _, out dataStart)
                || !string.Equals(storedId, windowId, StringComparison.Ordinal))
            {
                // Not ours, or a checksum collision on the name. Starting the file over is the only safe
                // move: appending our records to somebody else's file would corrupt both.
                return false;
            }

            var offset = (long)dataStart;
            while (offset < bytes.Length)
            {
                if (bytes.Length - offset < FrameOverheadBytes)
                {
                    break;
                }

                var declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)offset));
                var frame = declared + (long)FrameOverheadBytes;
                if (declared > MaxRecordBytes || offset + frame > bytes.Length)
                {
                    break;
                }

                offsets.Add(offset);
                offset += frame;
            }

            usable = offset;
            return true;
        }

        /// <summary>
        /// Options for creating a file owner-only. The mode is applied by the <c>open</c> that makes the
        /// inode — never a chmod afterwards, which would leave a window in which a world-readable file
        /// already held someone's chat. Windows takes no equivalent action, for the reason
        /// <c>SecretsStore.RestrictToOwner</c> gives at length: the file inherits a user-only ACL from
        /// <c>%APPDATA%</c>, and hand-rolling a DACL would be untestable code with a lockout for a
        /// failure mode.
        /// </summary>
        private static FileStreamOptions CreateOwnerOnly(FileShare share)
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = share,
            };

            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = OwnerOnlyFileMode;
            }

            return options;
        }

        private static void RestrictToOwner(string path, ILogger logger)
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                if (File.GetUnixFileMode(path) != OwnerOnlyFileMode)
                {
                    File.SetUnixFileMode(path, OwnerOnlyFileMode);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                logger.LogDebug(ex, "Narrowing the restore log file {Path} to 0600 failed", path);
            }
        }
    }
}
