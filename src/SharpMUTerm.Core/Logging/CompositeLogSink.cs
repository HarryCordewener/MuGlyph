using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Logging;

/// <summary>
/// Fans one session's output out to several sinks. <see cref="Configuration.LogFormat.Both"/> is the
/// reason it exists: a session holds a single <see cref="ILogSink"/>, so "plain and HTML" has to be
/// one sink that is two.
/// <para>
/// Every call reaches every sink, in order, even if an earlier one throws — a full disk on the plain
/// log must not silently stop the HTML one. The first failure is rethrown once the round is done, so
/// a broken sink is still reported rather than swallowed.
/// </para>
/// </summary>
public sealed class CompositeLogSink : ILogSink
{
    private readonly ILogSink[] _sinks;

    public CompositeLogSink(IEnumerable<ILogSink> sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        _sinks = sinks.ToArray();
        if (Array.IndexOf(_sinks, null) >= 0)
        {
            throw new ArgumentException("A composite log sink cannot hold a null sink.", nameof(sinks));
        }
    }

    /// <summary>The sinks this one writes through, in write order.</summary>
    public IReadOnlyList<ILogSink> Sinks => _sinks;

    public void WriteLine(StyledLine line) => ForEach(sink => sink.WriteLine(line));

    public void WriteSystem(string text) => ForEach(sink => sink.WriteSystem(text));

    public void Flush() => ForEach(sink => sink.Flush());

    public void Dispose() => ForEach(sink => sink.Dispose());

    private void ForEach(Action<ILogSink> action)
    {
        Exception? first = null;
        foreach (var sink in _sinks)
        {
            try
            {
                action(sink);
            }
            catch (Exception ex)
            {
                first ??= ex;
            }
        }

        if (first is not null)
        {
            throw first;
        }
    }
}
