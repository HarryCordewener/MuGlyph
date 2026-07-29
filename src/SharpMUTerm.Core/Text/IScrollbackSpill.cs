using Microsoft.Extensions.Logging;

namespace SharpMUTerm.Core.Text;

/// <summary>
/// Storage for lines evicted from a <see cref="ScrollbackBuffer"/>'s in-memory window, addressed by
/// the same absolute line indices the buffer uses (index 0 is the first line the buffer ever saw).
/// <para>
/// A spill holds the half-open index range <c>[FirstIndex, EndIndex)</c>. <see cref="Append"/> takes
/// lines in index order — the next line appended is always index <see cref="EndIndex"/> — and
/// <see cref="FirstIndex"/> advances as the store reclaims space, so the range it can serve is a
/// window that slides forward, never a hole in the middle.
/// </para>
/// <para>
/// <b>A spill must never throw for an I/O reason.</b> Scrollback is a convenience; a full disk or a
/// read-only home directory is not a reason to lose a live line or drop a session. Implementations
/// report a fault by going unhealthy (see <see cref="IsHealthy"/>), which empties their index range
/// and turns them into no-ops, and by logging it once through <see cref="Logger"/>.
/// </para>
/// </summary>
public interface IScrollbackSpill : IDisposable
{
    /// <summary>Where this store's diagnostics go. Set by the owning buffer; never null.</summary>
    ILogger Logger { get; set; }

    /// <summary>The absolute index of the oldest line still retrievable, or <see cref="EndIndex"/> when empty.</summary>
    long FirstIndex { get; }

    /// <summary>One past the absolute index of the newest stored line.</summary>
    long EndIndex { get; }

    /// <summary>How many lines are currently retrievable — <see cref="EndIndex"/> less <see cref="FirstIndex"/>.</summary>
    long Count { get; }

    /// <summary>
    /// False once the store has hit an unrecoverable I/O fault. It then holds nothing and does
    /// nothing, and the owning buffer is memory-only for the rest of the session.
    /// </summary>
    bool IsHealthy { get; }

    /// <summary>Stores the line at absolute index <see cref="EndIndex"/>. A no-op when unhealthy.</summary>
    void Append(StyledLine line);

    /// <summary>
    /// Appends the lines for indices <c>[start, start + count)</c> to <paramref name="into"/>, in
    /// index order, restricted to the intersection with <c>[FirstIndex, EndIndex)</c>. A record that
    /// cannot be read back — corrupted or truncated by something outside this process — yields
    /// <see cref="StyledLine.Empty"/> so the result stays contiguous and index-aligned: a paging view
    /// gets a blank row rather than a hole or an exception.
    /// </summary>
    void Read(long start, int count, List<StyledLine> into);

    /// <summary>Discards everything stored and resets the index range to zero.</summary>
    void Clear();
}
