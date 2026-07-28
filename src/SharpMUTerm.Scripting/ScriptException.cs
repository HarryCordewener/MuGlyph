using MoonSharp.Interpreter;

namespace SharpMUTerm.Scripting;

/// <summary>
/// A clean, MoonSharp-free error surfaced by the scripting layer. Wraps the underlying Lua
/// runtime or syntax error so callers never see raw <see cref="InterpreterException"/> types.
/// </summary>
public sealed class ScriptException : Exception
{
    public ScriptException(string message, int? line = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Line = line;
    }

    /// <summary>The 1-based Lua source line the error was reported on, if known.</summary>
    public int? Line { get; }

    /// <summary>Wraps a MoonSharp interpreter exception, preferring its decorated message.</summary>
    internal static ScriptException FromInterpreter(InterpreterException ex)
    {
        var message = string.IsNullOrEmpty(ex.DecoratedMessage) ? ex.Message : ex.DecoratedMessage;
        return new ScriptException(message, TryExtractLine(ex), ex);
    }

    private static int? TryExtractLine(InterpreterException ex)
    {
        var decorated = ex.DecoratedMessage;
        if (string.IsNullOrEmpty(decorated))
        {
            return null;
        }

        // Syntax errors: "chunk:(fromLine,fromCol-toLine,toCol) message". Anchor on the ":("
        // header marker so a stray '(' in the message text can't be mistaken for the range.
        var syntax = decorated.IndexOf(":(", StringComparison.Ordinal);
        if (syntax >= 0)
        {
            var start = syntax + 2;
            var comma = decorated.IndexOf(',', start);
            if (comma > start && int.TryParse(decorated.AsSpan(start, comma - start), out var syntaxLine))
            {
                return syntaxLine;
            }
        }

        // Runtime errors: "[string \"chunk\"]:LINE: message". Anchor on the "]:" header marker
        // (not the last ']', which may appear inside the message, e.g. table["x"]).
        var runtime = decorated.IndexOf("]:", StringComparison.Ordinal);
        if (runtime >= 0)
        {
            var start = runtime + 2;
            var colon = decorated.IndexOf(':', start);
            if (colon > start && int.TryParse(decorated.AsSpan(start, colon - start), out var runtimeLine))
            {
                return runtimeLine;
            }
        }

        return null;
    }
}
