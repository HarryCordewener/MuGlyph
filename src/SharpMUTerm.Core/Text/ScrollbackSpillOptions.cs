namespace SharpMUTerm.Core.Text;

/// <summary>
/// How far scrollback may spill to disk, and where. Part of <c>AppConfiguration</c>, so these are
/// user-editable settings; the sizes are in whole megabytes and lines because that is what a person
/// reasons about in a config file.
/// <para>
/// The spill is an <b>ephemeral per-session cache</b>, not a transcript: see
/// <see cref="FileScrollbackSpill"/> for the lifetime, the location, and why it is emphatically not
/// the same thing as the user-facing session logs.
/// </para>
/// </summary>
public sealed class ScrollbackSpillOptions
{
    /// <summary>
    /// Whether lines evicted from the in-memory window are kept on disk so the view can scroll
    /// further back than <c>ScrollbackLines</c>. When false the buffer is memory-only and nothing is
    /// ever written — the escape hatch for a user who wants no session text on disk at all.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum lines held on disk. Reaching it drops the oldest segment, so the achievable scroll
    /// depth oscillates between roughly <c>MaxLines - SegmentMegabytes</c>' worth of lines and
    /// <see cref="MaxLines"/>; that is the price of reclaiming space without rewriting the file.
    /// </summary>
    public int MaxLines { get; set; } = 200_000;

    /// <summary>Maximum total size of the spill, in megabytes. Whichever bound trips first wins.</summary>
    public int MaxMegabytes { get; set; } = 64;

    /// <summary>
    /// Size of one spill segment, in megabytes. Space is reclaimed a whole segment at a time (an
    /// <c>unlink</c>, not a rewrite), so this is the granularity of <see cref="MaxLines"/> and
    /// <see cref="MaxMegabytes"/> — smaller is tighter but means more open file handles.
    /// </summary>
    public int SegmentMegabytes { get; set; } = 4;

    /// <summary>
    /// Root directory for spill caches. Null (the default) means the platform's per-user cache
    /// directory — <c>$XDG_CACHE_HOME</c> or <c>~/.cache</c> on Linux, <c>%LOCALAPPDATA%</c> on
    /// Windows, <c>~/Library/Caches</c> on macOS. Each store gets its own subdirectory beneath it,
    /// so two instances of the app never share a file.
    /// </summary>
    public string? Directory { get; set; }
}
