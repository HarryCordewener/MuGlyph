using MuClient.Core.Text;

namespace MuClient.Core.Logging;

/// <summary>A destination for session output logging (plain text or HTML).</summary>
public interface ILogSink : IDisposable
{
    /// <summary>Writes one styled output line to the log.</summary>
    void WriteLine(StyledLine line);

    /// <summary>Writes a client-generated informational line (e.g. "*** Connected").</summary>
    void WriteSystem(string text);

    /// <summary>Flushes buffered output to the underlying store.</summary>
    void Flush();
}
