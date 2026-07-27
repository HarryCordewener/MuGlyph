using MuClient.Core.Text;

namespace MuClient.Core.Logging;

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
    public static PlainTextLogSink CreateFile(string path, bool append = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var stream = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
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
