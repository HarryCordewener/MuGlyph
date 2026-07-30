namespace SharpMUTerm.Core.Text;

/// <summary>
/// How much of each pane's recent output survives a restart, and where it is kept. Part of
/// <c>AppConfiguration</c>, so these are user-editable settings.
/// <para>
/// The restore log is the one piece of session text in this client that is <b>kept on purpose</b> and
/// is <b>not</b> a transcript. The other two are neither: <see cref="ScrollbackSpillOptions"/>'s spill
/// is an ephemeral cache purged on the next launch, and <c>PlainTextLogSink</c>/<c>HtmlLogSink</c> are
/// opt-in transcripts a user keeps and reads. This exists so that restarting the client does not
/// empty every pane — see <see cref="RestoreLog"/>.
/// </para>
/// </summary>
public sealed class RestoreLogOptions
{
    /// <summary>
    /// The default per-window bound, in <b>lines</b>. See <see cref="MaxLinesPerWindow"/> for the
    /// argument; it is named here so a test can assert the shipped value rather than a literal.
    /// </summary>
    public const int DefaultMaxLinesPerWindow = 500;

    /// <summary>The directory name, under the configuration directory, holding the per-window logs.</summary>
    public const string DirectoryName = "restore";

    /// <summary>
    /// Whether panes come back with their previous session's content. On by default — a client whose
    /// channel windows are empty after a restart is the state this feature exists to remove. Switching
    /// it off here stops <em>all</em> of it; a single character opts out on F9 instead
    /// (<see cref="SharpMUTerm.Core.Configuration.LoggingSettings.RestoreLog"/>).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How many lines of each window are kept, and so how many come back.
    /// <para>
    /// <b>Lines, never bytes.</b> The design notes record BeipMU hanging for ever trying to make room in
    /// a byte-bounded ring when one block was larger than the whole ring
    /// (<c>docs/superpowers/specs/2026-07-30-scrollback-design.md</c>). A line bound cannot reach that
    /// state: dropping one line always makes room for one line, whatever either of them weighs.
    /// </para>
    /// <para>
    /// <b>Why 500.</b> It is deliberately far under the live 10,000-line window
    /// (<see cref="ScrollbackBuffer.DefaultCapacity"/>) because the two answer different questions. The
    /// live buffer is "how far can I scroll back in this session"; this is "what was on screen when I
    /// quit", and 500 lines is ten to sixteen paneful screens — more context than anyone re-reads after
    /// a restart, and enough that a quiet channel window still has its last conversation in it. It is
    /// also what makes the cost bearable at both ends: at roughly 120 bytes a line encoded it is 60 KB
    /// per window, so a dozen windows sit under a megabyte beside <c>config.json</c>, and the startup
    /// read that runs before the first frame stays in the low milliseconds. Raising it costs startup
    /// latency on every launch to buy scrollback depth the live buffer already provides for the session
    /// you are actually in.
    /// </para>
    /// </summary>
    public int MaxLinesPerWindow { get; set; } = DefaultMaxLinesPerWindow;

    /// <summary>
    /// Where the logs live. Null (the default) means a <c>restore</c> directory inside the configuration
    /// directory, beside <c>config.json</c> and <c>secrets.json</c> — a data location, not a cache one,
    /// because unlike the spill these files are meant to survive.
    /// </summary>
    public string? Directory { get; set; }
}
