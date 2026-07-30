using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Logging;

/// <summary>Writes plain, unstyled output lines to a <see cref="TextWriter"/>.</summary>
public sealed class PlainTextLogSink : ILogSink
{
    private readonly TextWriter _writer;
    private readonly bool _ownsWriter;
    private readonly object _gate = new();

    public PlainTextLogSink(TextWriter writer, bool ownsWriter = true)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _ownsWriter = ownsWriter;
    }

    /// <summary>Opens a plain-text log file, creating parent directories as needed.</summary>
    /// <remarks>
    /// The share mode names <see cref="FileShare.Delete"/> deliberately: a transcript belongs to the user,
    /// and on Windows a file opened without it can be neither deleted nor renamed nor its directory
    /// removed until the client exits — so tidying up or rotating a log mid-session failed with a sharing
    /// violation there and worked on Linux, which ignores share modes.
    /// </remarks>
    public static PlainTextLogSink CreateFile(string path, bool append = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var stream = new FileStream(
            path,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.Read | FileShare.Delete);
        return new PlainTextLogSink(new StreamWriter(stream) { AutoFlush = false });
    }

    public void WriteLine(StyledLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
        {
            _writer.WriteLine(line.Text);
        }
    }

    public void WriteSystem(string text)
    {
        lock (_gate)
        {
            _writer.WriteLine(text);
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Flush();
            if (_ownsWriter)
            {
                _writer.Dispose();
            }
        }
    }
}
