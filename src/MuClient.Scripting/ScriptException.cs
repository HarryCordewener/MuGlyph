using MoonSharp.Interpreter;

namespace MuClient.Scripting;

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
        // MoonSharp encodes location as "chunk:(fromLine,fromCol-toLine,toCol)" in DecoratedMessage.
        var decorated = ex.DecoratedMessage;
        if (string.IsNullOrEmpty(decorated))
        {
            return null;
        }

        var open = decorated.IndexOf('(');
        if (open < 0 || open + 1 >= decorated.Length)
        {
            return null;
        }

        var comma = decorated.IndexOf(',', open);
        if (comma < 0)
        {
            return null;
        }

        return int.TryParse(decorated.AsSpan(open + 1, comma - open - 1), out var line) ? line : null;
    }
}
