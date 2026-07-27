using System.Text;

namespace MuClient.Core.Text;

/// <summary>
/// Incremental, line-oriented ANSI/VT parser. Feed it decoded text (any number of
/// chunks); it emits fully-terminated <see cref="StyledLine"/>s and retains the
/// in-progress line plus the current <see cref="TextStyle"/> across calls, so escape
/// sequences and colour state may span chunk boundaries.
///
/// SGR handling covers the 16 base colours, the 256-colour palette (<c>38;5;n</c>),
/// 24-bit truecolour (<c>38;2;r;g;b</c>, including the colon-delimited ISO form), and
/// the common rendition attributes. Non-SGR CSI sequences (cursor movement, erase),
/// OSC strings, and other escapes are recognised and discarded rather than leaking
/// into the output as stray text.
/// </summary>
public sealed class AnsiParser
{
    private const int MaxSequenceLength = 128;

    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        Csi,
        Osc,
        OscEscape,
    }

    private State _state = State.Ground;
    private TextStyle _current = TextStyle.Default;
    private readonly StringBuilder _run = new();
    private readonly List<StyledSpan> _lineSpans = new();
    private readonly StringBuilder _seq = new();

    /// <summary>The rendition state that will apply to the next printed character.</summary>
    public TextStyle CurrentStyle => _current;

    /// <summary>True when a partial line or escape sequence is buffered.</summary>
    public bool HasPendingContent => _run.Length > 0 || _lineSpans.Count > 0 || _state != State.Ground;

    /// <summary>Feeds a chunk of text, returning every line completed by a newline within it.</summary>
    public IReadOnlyList<StyledLine> Feed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Feed(text.AsSpan());
    }

    /// <summary>Feeds a chunk of text, returning every line completed by a newline within it.</summary>
    public IReadOnlyList<StyledLine> Feed(ReadOnlySpan<char> text)
    {
        List<StyledLine>? lines = null;
        foreach (var ch in text)
        {
            Process(ch, ref lines);
        }

        return (IReadOnlyList<StyledLine>?)lines ?? Array.Empty<StyledLine>();
    }

    /// <summary>
    /// Returns the buffered partial line (e.g. a prompt not terminated by a newline) and
    /// clears it, or <c>null</c> if nothing is buffered. Colour state is preserved.
    /// </summary>
    public StyledLine? Flush()
    {
        FlushRun();
        if (_lineSpans.Count == 0)
        {
            return null;
        }

        var line = new StyledLine(_lineSpans);
        _lineSpans.Clear();
        return line;
    }

    /// <summary>Resets all parser state, including the current style.</summary>
    public void Reset()
    {
        _state = State.Ground;
        _current = TextStyle.Default;
        _run.Clear();
        _lineSpans.Clear();
        _seq.Clear();
    }

    private void Process(char ch, ref List<StyledLine>? lines)
    {
        switch (_state)
        {
            case State.Ground:
                ProcessGround(ch, ref lines);
                break;

            case State.Escape:
                ProcessEscape(ch);
                break;

            case State.EscapeIntermediate:
                // Consume the single trailing byte of e.g. ESC ( B and return to ground.
                _state = State.Ground;
                break;

            case State.Csi:
                ProcessCsi(ch);
                break;

            case State.Osc:
                ProcessOsc(ch);
                break;

            case State.OscEscape:
                // Inside an OSC string we saw ESC; a following '\' is the ST terminator.
                _state = ch == '\\' ? State.Ground : State.Osc;
                break;
        }
    }

    private void ProcessGround(char ch, ref List<StyledLine>? lines)
    {
        switch (ch)
        {
            case '\x1b':
                _state = State.Escape;
                break;

            case '\n':
                CompleteLine(ref lines);
                break;

            case '\r':
                // Carriage returns are dropped; scrollback is line-based.
                break;

            default:
                _run.Append(ch);
                break;
        }
    }

    private void ProcessEscape(char ch)
    {
        switch (ch)
        {
            case '[':
                _seq.Clear();
                _state = State.Csi;
                break;

            case ']':
                _seq.Clear();
                _state = State.Osc;
                break;

            case '(':
            case ')':
            case '*':
            case '+':
            case '#':
            case '%':
                _state = State.EscapeIntermediate;
                break;

            default:
                // Two-byte escape (ESC c, ESC M, ...) — consumed and ignored.
                _state = State.Ground;
                break;
        }
    }

    private void ProcessCsi(char ch)
    {
        // Final byte is in the range 0x40-0x7E.
        if (ch is >= '\x40' and <= '\x7e')
        {
            if (ch == 'm')
            {
                ApplySgr(_seq.ToString());
            }

            // All other CSI sequences (cursor, erase, ...) are discarded.
            _seq.Clear();
            _state = State.Ground;
            return;
        }

        // Parameter (0x30-0x3F) and intermediate (0x20-0x2F) bytes accumulate.
        if (ch is >= '\x20' and <= '\x3f')
        {
            if (_seq.Length < MaxSequenceLength)
            {
                _seq.Append(ch);
            }

            return;
        }

        // Anything else (e.g. an embedded control char) aborts the malformed sequence.
        _seq.Clear();
        _state = State.Ground;
    }

    private void ProcessOsc(char ch)
    {
        switch (ch)
        {
            case '\x07': // BEL terminator
                _state = State.Ground;
                break;

            case '\x1b': // possible ST (ESC \)
                _state = State.OscEscape;
                break;

            default:
                if (_seq.Length < MaxSequenceLength)
                {
                    _seq.Append(ch);
                }

                break;
        }
    }

    private void CompleteLine(ref List<StyledLine>? lines)
    {
        FlushRun();
        var line = _lineSpans.Count == 0 ? StyledLine.Empty : new StyledLine(_lineSpans);
        _lineSpans.Clear();
        (lines ??= new List<StyledLine>()).Add(line);
    }

    private void FlushRun()
    {
        if (_run.Length == 0)
        {
            return;
        }

        _lineSpans.Add(new StyledSpan(_run.ToString(), _current));
        _run.Clear();
    }

    private void ApplySgr(string parameters)
    {
        // Text accumulated so far keeps the pre-change style.
        FlushRun();

        if (parameters.Length == 0)
        {
            _current = TextStyle.Default;
            return;
        }

        var tokens = parameters.Split(';');
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];

            // Colon-delimited extended colour, e.g. "38:5:196" or "38:2:255:0:0".
            if (token.IndexOf(':') >= 0)
            {
                ApplyColonColor(token);
                continue;
            }

            if (!int.TryParse(token, out var code))
            {
                code = 0; // An empty parameter is treated as 0 (reset).
            }

            switch (code)
            {
                case 0:
                    _current = TextStyle.Default;
                    break;
                case 1:
                    _current = _current.AddAttribute(TextAttributes.Bold);
                    break;
                case 2:
                    _current = _current.AddAttribute(TextAttributes.Faint);
                    break;
                case 3:
                    _current = _current.AddAttribute(TextAttributes.Italic);
                    break;
                case 4:
                    _current = _current.AddAttribute(TextAttributes.Underline);
                    break;
                case 5:
                case 6:
                    _current = _current.AddAttribute(TextAttributes.Blink);
                    break;
                case 7:
                    _current = _current.AddAttribute(TextAttributes.Reverse);
                    break;
                case 8:
                    _current = _current.AddAttribute(TextAttributes.Conceal);
                    break;
                case 9:
                    _current = _current.AddAttribute(TextAttributes.Strikethrough);
                    break;
                case 21:
                case 22:
                    _current = _current.RemoveAttribute(TextAttributes.Bold | TextAttributes.Faint);
                    break;
                case 23:
                    _current = _current.RemoveAttribute(TextAttributes.Italic);
                    break;
                case 24:
                    _current = _current.RemoveAttribute(TextAttributes.Underline);
                    break;
                case 25:
                    _current = _current.RemoveAttribute(TextAttributes.Blink);
                    break;
                case 27:
                    _current = _current.RemoveAttribute(TextAttributes.Reverse);
                    break;
                case 28:
                    _current = _current.RemoveAttribute(TextAttributes.Conceal);
                    break;
                case 29:
                    _current = _current.RemoveAttribute(TextAttributes.Strikethrough);
                    break;
                case >= 30 and <= 37:
                    _current = _current.WithForeground(TerminalColor.FromIndex(code - 30));
                    break;
                case 38:
                    _current = _current.WithForeground(ParseExtendedColor(tokens, ref i) ?? _current.Foreground);
                    break;
                case 39:
                    _current = _current.WithForeground(TerminalColor.Default);
                    break;
                case >= 40 and <= 47:
                    _current = _current.WithBackground(TerminalColor.FromIndex(code - 40));
                    break;
                case 48:
                    _current = _current.WithBackground(ParseExtendedColor(tokens, ref i) ?? _current.Background);
                    break;
                case 49:
                    _current = _current.WithBackground(TerminalColor.Default);
                    break;
                case >= 90 and <= 97:
                    _current = _current.WithForeground(TerminalColor.FromIndex(code - 90 + 8));
                    break;
                case >= 100 and <= 107:
                    _current = _current.WithBackground(TerminalColor.FromIndex(code - 100 + 8));
                    break;
                default:
                    // Unknown SGR code — ignored.
                    break;
            }
        }
    }

    /// <summary>
    /// Parses the semicolon-form extended colour that follows a 38/48 code, advancing
    /// <paramref name="i"/> past the consumed tokens. Returns null if malformed.
    /// </summary>
    private static TerminalColor? ParseExtendedColor(string[] tokens, ref int i)
    {
        if (i + 1 >= tokens.Length || !int.TryParse(tokens[i + 1], out var mode))
        {
            return null;
        }

        switch (mode)
        {
            case 5 when i + 2 < tokens.Length && int.TryParse(tokens[i + 2], out var idx) && idx is >= 0 and <= 255:
                i += 2;
                return TerminalColor.FromIndex(idx);

            case 2 when i + 4 < tokens.Length &&
                        byte.TryParse(tokens[i + 2], out var r) &&
                        byte.TryParse(tokens[i + 3], out var g) &&
                        byte.TryParse(tokens[i + 4], out var b):
                i += 4;
                return TerminalColor.FromRgb(r, g, b);

            default:
                return null;
        }
    }

    /// <summary>Parses a single colon-delimited extended colour token and applies it.</summary>
    private void ApplyColonColor(string token)
    {
        var parts = token.Split(':');
        if (parts.Length < 3 || !int.TryParse(parts[0], out var target))
        {
            return;
        }

        var isForeground = target == 38;
        if (target is not (38 or 48))
        {
            return;
        }

        if (!int.TryParse(parts[1], out var mode))
        {
            return;
        }

        TerminalColor? colour = null;
        if (mode == 5 && int.TryParse(parts[2], out var idx) && idx is >= 0 and <= 255)
        {
            colour = TerminalColor.FromIndex(idx);
        }
        else if (mode == 2 && parts.Length >= 5)
        {
            // ISO form may carry a colour-space id at parts[2]; the RGB triple is the last three.
            var baseIndex = parts.Length >= 6 ? parts.Length - 3 : 2;
            if (byte.TryParse(parts[baseIndex], out var r) &&
                byte.TryParse(parts[baseIndex + 1], out var g) &&
                byte.TryParse(parts[baseIndex + 2], out var b))
            {
                colour = TerminalColor.FromRgb(r, g, b);
            }
        }

        if (colour is null)
        {
            return;
        }

        _current = isForeground ? _current.WithForeground(colour.Value) : _current.WithBackground(colour.Value);
    }
}
