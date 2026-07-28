using System.Globalization;
using System.Text;
using SharpMUTerm.Core.Text;

namespace SharpMUTerm.Core.Protocols;

/// <summary>
/// Incremental, stateful, line-oriented parser for the MUD eXtension Protocol (MXP).
/// Feed it decoded text (any number of chunks); it emits fully-terminated
/// <see cref="StyledLine"/>s and retains the in-progress line, the current
/// <see cref="TextStyle"/>, and the stack of open tags across calls, so a tag
/// (<c>&lt;...&gt;</c>) or entity (<c>&amp;...;</c>) may straddle a chunk boundary.
///
/// Mirrors the shape of <see cref="AnsiParser"/>: <see cref="Feed(string)"/> returns the
/// lines completed within the chunk, <see cref="Flush"/> yields a buffered partial line
/// (e.g. an unterminated prompt), and colour/attribute state is preserved between calls.
///
/// <para>Scope notes for v1:</para>
/// <list type="bullet">
/// <item>Input is treated as MXP "secure/open" line mode — every tag is processed. The
/// telnet MXP line-mode security state machine (mode-change tags <c>&lt;RESET&gt;</c>,
/// line tags <c>[1z]</c> etc.) is <b>not</b> implemented here.</item>
/// <item>Inline <c>&lt;IMG&gt;</c> rendering is out of scope (graphics live elsewhere); the
/// tag is parsed and ignored gracefully.</item>
/// <item>Raw ANSI SGR is assumed to have been handled upstream. An ESC byte (0x1b) seen
/// here is passed through untouched into the span text.</item>
/// <item>Unknown/unsupported tags (<c>&lt;VAR&gt;</c>, <c>&lt;EXPIRE&gt;</c>, <c>&lt;H1&gt;</c>,
/// <c>&lt;P&gt;</c>, …) are consumed and discarded — they never leak into the output.</item>
/// <item>A stray <c>&lt;</c> that cannot begin a tag, or a stray <c>&amp;</c> that cannot begin
/// an entity, is emitted literally (xterm-like leniency). Tag/entity buffers are length
/// capped to avoid runaway on malformed input.</item>
/// </list>
/// </summary>
public sealed class MxpParser : ILineParser
{
    private const int MaxTagLength = 4096;
    private const int MaxEntityLength = 32;

    private enum Mode
    {
        Text,
        Tag,
        Entity,
    }

    /// <summary>A single open MXP element on the tag stack.</summary>
    private sealed class Frame
    {
        public required string Name { get; init; }
        public TextStyle SavedStyle { get; init; }
        public SpanInteraction? SavedInteraction { get; init; }
        public bool IsInteraction { get; init; }
        public bool IsLink { get; init; }
        public bool DeferCommand { get; init; }
        public string? Hint { get; init; }
        public bool PromptOnly { get; init; }
        public int SpanStart { get; init; }
    }

    private Mode _mode = Mode.Text;
    private TextStyle _current = TextStyle.Default;
    private SpanInteraction? _interaction;
    private readonly StringBuilder _run = new();
    private readonly StringBuilder _tag = new();
    private readonly StringBuilder _entity = new();
    private readonly List<StyledSpan> _lineSpans = new();
    private readonly List<Frame> _stack = new();
    private List<StyledLine>? _emit;

    /// <summary>The rendition state that will apply to the next printed character.</summary>
    public TextStyle CurrentStyle => _current;

    /// <summary>True when a partial line, an open tag/entity, or unclosed markup is buffered.</summary>
    public bool HasPendingContent =>
        _run.Length > 0 || _lineSpans.Count > 0 || _mode != Mode.Text;

    /// <summary>Feeds a chunk of text, returning every line completed by a newline (or <c>&lt;BR&gt;</c>) within it.</summary>
    public IReadOnlyList<StyledLine> Feed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Feed(text.AsSpan());
    }

    /// <summary>Feeds a chunk of text, returning every line completed by a newline (or <c>&lt;BR&gt;</c>) within it.</summary>
    public IReadOnlyList<StyledLine> Feed(ReadOnlySpan<char> text)
    {
        _emit = null;
        foreach (var ch in text)
        {
            Process(ch);
        }

        var result = (IReadOnlyList<StyledLine>?)_emit ?? Array.Empty<StyledLine>();
        _emit = null;
        return result;
    }

    /// <summary>
    /// Returns the buffered partial line (e.g. a prompt not terminated by a newline) and
    /// clears it, or <c>null</c> if nothing is buffered. Any open interaction is finalised
    /// so a flushed prompt's clickable spans carry their command; colour/attribute state and
    /// formatting tags are preserved.
    /// </summary>
    public StyledLine? Flush()
    {
        FlushRun();
        CloseInteractionsAtBoundary();
        if (_lineSpans.Count == 0)
        {
            return null;
        }

        var line = new StyledLine(_lineSpans);
        _lineSpans.Clear();
        return line;
    }

    /// <summary>Resets all parser state, including the current style and the open-tag stack.</summary>
    public void Reset()
    {
        _mode = Mode.Text;
        _current = TextStyle.Default;
        _interaction = null;
        _run.Clear();
        _tag.Clear();
        _entity.Clear();
        _lineSpans.Clear();
        _stack.Clear();
    }

    private void Process(char ch)
    {
        switch (_mode)
        {
            case Mode.Text:
                ProcessText(ch);
                break;

            case Mode.Tag:
                ProcessTagChar(ch);
                break;

            case Mode.Entity:
                ProcessEntityChar(ch);
                break;
        }
    }

    private void ProcessText(char ch)
    {
        switch (ch)
        {
            case '<':
                _tag.Clear();
                _mode = Mode.Tag;
                break;

            case '&':
                _entity.Clear();
                _mode = Mode.Entity;
                break;

            case '\n':
                CompleteLine();
                break;

            case '\r':
                // Carriage returns are dropped; scrollback is line-based.
                break;

            default:
                // Anything else (including a passed-through ESC 0x1b) is literal text.
                _run.Append(ch);
                break;
        }
    }

    private void ProcessTagChar(char ch)
    {
        if (_tag.Length == 0)
        {
            // Decide whether the '<' actually begins a tag.
            if (ch == '>')
            {
                // Empty "<>" — not a tag; emit literally.
                _run.Append('<').Append('>');
                _mode = Mode.Text;
                return;
            }

            if (!(char.IsLetter(ch) || ch == '/' || ch == '!'))
            {
                // Stray '<' (e.g. "a < b") — emit it literally and reprocess this char.
                _run.Append('<');
                _mode = Mode.Text;
                ProcessText(ch);
                return;
            }

            _tag.Append(ch);
            return;
        }

        switch (ch)
        {
            case '>':
                ProcessTag(_tag.ToString());
                _tag.Clear();
                _mode = Mode.Text;
                break;

            case '\n':
                // A newline inside a "tag" means it was never a tag; bail out literally.
                _run.Append('<').Append(_tag);
                _tag.Clear();
                _mode = Mode.Text;
                ProcessText('\n');
                break;

            default:
                if (_tag.Length >= MaxTagLength)
                {
                    _run.Append('<').Append(_tag);
                    _tag.Clear();
                    _mode = Mode.Text;
                    ProcessText(ch);
                    return;
                }

                _tag.Append(ch);
                break;
        }
    }

    private void ProcessEntityChar(char ch)
    {
        if (ch == ';')
        {
            var content = _entity.ToString();
            _entity.Clear();
            _mode = Mode.Text;

            var replacement = ResolveEntity(content);
            if (replacement is not null)
            {
                _run.Append(replacement);
            }
            else
            {
                // Unknown entity — emit the raw text so nothing is lost.
                _run.Append('&').Append(content).Append(';');
            }

            return;
        }

        if ((char.IsLetterOrDigit(ch) || ch == '#') && _entity.Length < MaxEntityLength)
        {
            _entity.Append(ch);
            return;
        }

        // Not a valid entity (stray '&' or malformed) — emit literally and reprocess.
        _run.Append('&').Append(_entity);
        _entity.Clear();
        _mode = Mode.Text;
        ProcessText(ch);
    }

    private static string? ResolveEntity(string content)
    {
        if (content.Length == 0)
        {
            return null;
        }

        if (content[0] == '#')
        {
            var digits = content.AsSpan(1);
            int code;
            if (digits.Length > 1 && (digits[0] == 'x' || digits[0] == 'X'))
            {
                if (!int.TryParse(digits[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                {
                    return null;
                }
            }
            else if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
            {
                return null;
            }

            if (code < 0 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF))
            {
                return null;
            }

            return char.ConvertFromUtf32(code);
        }

        return content.ToLowerInvariant() switch
        {
            "lt" => "<",
            "gt" => ">",
            "amp" => "&",
            "quot" => "\"",
            "apos" => "'",
            "nbsp" => " ",
            _ => null,
        };
    }

    private void ProcessTag(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        if (trimmed[0] == '/')
        {
            CloseTag(Canonical(trimmed[1..].Trim()));
            return;
        }

        // Tolerate a self-closing slash, e.g. "<BR/>".
        if (trimmed[^1] == '/')
        {
            trimmed = trimmed[..^1].TrimEnd();
            if (trimmed.Length == 0)
            {
                return;
            }
        }

        // Split "NAME rest-of-attributes".
        var nameEnd = 0;
        while (nameEnd < trimmed.Length && !char.IsWhiteSpace(trimmed[nameEnd]))
        {
            nameEnd++;
        }

        var name = Canonical(trimmed[..nameEnd]);
        var attrs = nameEnd < trimmed.Length ? trimmed[(nameEnd + 1)..] : string.Empty;
        HandleOpener(name, attrs);
    }

    private void HandleOpener(string name, string attrs)
    {
        switch (name)
        {
            case "B":
                OpenFormatting(name, TextAttributes.Bold);
                break;
            case "I":
                OpenFormatting(name, TextAttributes.Italic);
                break;
            case "U":
                OpenFormatting(name, TextAttributes.Underline);
                break;
            case "S":
                OpenFormatting(name, TextAttributes.Strikethrough);
                break;
            case "COLOR":
            case "FONT":
                OpenColor(name, attrs);
                break;
            case "SEND":
                OpenSend(attrs);
                break;
            case "A":
                OpenLink(attrs);
                break;
            case "BR":
                CompleteLine();
                break;
            default:
                // Unknown/unsupported tag (VAR, EXPIRE, IMG, H1, P, …) — consumed and ignored.
                break;
        }
    }

    private void OpenFormatting(string name, TextAttributes attribute)
    {
        PushFrame(name, isInteraction: false, isLink: false, deferCommand: false, hint: null, promptOnly: false);
        _current = _current.AddAttribute(attribute);
    }

    private void OpenColor(string name, string attrs)
    {
        var parsed = ParseAttributes(attrs);
        // FONT carries its foreground on COLOR=; COLOR/C use FORE=. Positional first = fore.
        var fore = GetAttr(parsed, "FORE") ?? GetAttr(parsed, "COLOR") ?? Positional(parsed, 0);
        var back = GetAttr(parsed, "BACK") ?? Positional(parsed, 1);

        PushFrame(name, isInteraction: false, isLink: false, deferCommand: false, hint: null, promptOnly: false);

        if (fore is not null && WebColors.TryParse(fore, out var fg))
        {
            _current = _current.WithForeground(fg);
        }

        if (back is not null && WebColors.TryParse(back, out var bg))
        {
            _current = _current.WithBackground(bg);
        }
    }

    private void OpenSend(string attrs)
    {
        var parsed = ParseAttributes(attrs);
        var href = GetAttr(parsed, "HREF") ?? Positional(parsed, 0);
        var hint = GetAttr(parsed, "HINT") ?? Positional(parsed, 1);
        var prompt = HasFlag(parsed, "PROMPT");

        if (href is not null)
        {
            // HREF may hold several '|'-separated commands; the first is the primary command.
            var primary = href.Split('|', 2)[0];
            PushFrame("SEND", isInteraction: true, isLink: false, deferCommand: false, hint: hint, promptOnly: prompt);
            _interaction = SpanInteraction.Command(primary, hint, prompt);
        }
        else
        {
            // No HREF: the enclosed text is itself the command (resolved when the tag closes).
            PushFrame("SEND", isInteraction: true, isLink: false, deferCommand: true, hint: hint, promptOnly: prompt);
            _interaction = SpanInteraction.Command(string.Empty, hint, prompt);
        }
    }

    private void OpenLink(string attrs)
    {
        var parsed = ParseAttributes(attrs);
        var href = GetAttr(parsed, "HREF") ?? Positional(parsed, 0);
        var hint = GetAttr(parsed, "HINT") ?? Positional(parsed, 1);

        if (href is not null)
        {
            PushFrame("A", isInteraction: true, isLink: true, deferCommand: false, hint: hint, promptOnly: false);
            _interaction = SpanInteraction.Link(href, hint);
        }
        else
        {
            PushFrame("A", isInteraction: true, isLink: true, deferCommand: true, hint: hint, promptOnly: false);
            _interaction = SpanInteraction.Link(string.Empty, hint);
        }
    }

    private void PushFrame(string name, bool isInteraction, bool isLink, bool deferCommand, string? hint, bool promptOnly)
    {
        FlushRun();
        _stack.Add(new Frame
        {
            Name = name,
            SavedStyle = _current,
            SavedInteraction = _interaction,
            IsInteraction = isInteraction,
            IsLink = isLink,
            DeferCommand = deferCommand,
            Hint = hint,
            PromptOnly = promptOnly,
            SpanStart = _lineSpans.Count,
        });
    }

    private void CloseTag(string name)
    {
        var idx = -1;
        for (var i = _stack.Count - 1; i >= 0; i--)
        {
            if (_stack[i].Name == name)
            {
                idx = i;
                break;
            }
        }

        if (idx < 0)
        {
            // Stray/unbalanced closer — ignored (does not throw).
            return;
        }

        FlushRun();

        var matched = _stack[idx];
        for (var i = _stack.Count - 1; i >= idx; i--)
        {
            var frame = _stack[i];
            if (frame.IsInteraction && frame.DeferCommand)
            {
                FinalizeDeferredInteraction(frame);
            }

            _stack.RemoveAt(i);
        }

        // Restoring the matched frame's saved state reverts everything opened at or after it.
        _current = matched.SavedStyle;
        _interaction = matched.SavedInteraction;
    }

    /// <summary>
    /// Rewrites the spans emitted since a deferred <c>&lt;SEND&gt;</c>/<c>&lt;A&gt;</c> opened,
    /// using their concatenated text as the command/URL target.
    /// </summary>
    private void FinalizeDeferredInteraction(Frame frame)
    {
        if (frame.SpanStart >= _lineSpans.Count)
        {
            return;
        }

        var sb = new StringBuilder();
        for (var i = frame.SpanStart; i < _lineSpans.Count; i++)
        {
            sb.Append(_lineSpans[i].Text);
        }

        var target = sb.ToString();
        var interaction = frame.IsLink
            ? SpanInteraction.Link(target, frame.Hint)
            : SpanInteraction.Command(target, frame.Hint, frame.PromptOnly);

        for (var i = frame.SpanStart; i < _lineSpans.Count; i++)
        {
            var span = _lineSpans[i];
            _lineSpans[i] = new StyledSpan(span.Text, span.Style, interaction);
        }
    }

    /// <summary>
    /// Finalises and removes any open interaction (<c>&lt;SEND&gt;</c>/<c>&lt;A&gt;</c>) frames at a
    /// line/prompt boundary so a bare, unclosed interaction never leaks to the next line.
    /// Formatting/colour frames persist across the boundary (MXP tags may span lines).
    /// </summary>
    private void CloseInteractionsAtBoundary()
    {
        for (var i = _stack.Count - 1; i >= 0; i--)
        {
            var frame = _stack[i];
            if (!frame.IsInteraction)
            {
                continue;
            }

            if (frame.DeferCommand)
            {
                FinalizeDeferredInteraction(frame);
            }

            _stack.RemoveAt(i);
        }

        // A formatting frame opened *inside* an interaction still holds it in SavedInteraction;
        // if it closes on a later line, CloseTag would resurrect the ended interaction. Clear it
        // (and rebase SpanStart, which pointed into the now-cleared line) on surviving frames.
        for (var i = 0; i < _stack.Count; i++)
        {
            var frame = _stack[i];
            if (frame.SavedInteraction is not null || frame.SpanStart != 0)
            {
                _stack[i] = new Frame
                {
                    Name = frame.Name,
                    SavedStyle = frame.SavedStyle,
                    SavedInteraction = null,
                    IsInteraction = frame.IsInteraction,
                    IsLink = frame.IsLink,
                    DeferCommand = frame.DeferCommand,
                    Hint = frame.Hint,
                    PromptOnly = frame.PromptOnly,
                    SpanStart = 0,
                };
            }
        }

        _interaction = null;
    }

    private void CompleteLine()
    {
        FlushRun();
        CloseInteractionsAtBoundary();

        var line = _lineSpans.Count == 0 ? StyledLine.Empty : new StyledLine(_lineSpans);
        _lineSpans.Clear();
        (_emit ??= new List<StyledLine>()).Add(line);
    }

    private void FlushRun()
    {
        if (_run.Length == 0)
        {
            return;
        }

        _lineSpans.Add(new StyledSpan(_run.ToString(), _current, _interaction));
        _run.Clear();
    }

    private static string Canonical(string name) => name.ToUpperInvariant() switch
    {
        "B" or "BOLD" or "STRONG" => "B",
        "I" or "ITALIC" or "EM" => "I",
        "U" or "UNDERLINE" => "U",
        "S" or "STRIKEOUT" => "S",
        "C" or "COLOR" => "COLOR",
        var other => other,
    };

    // ----- Attribute parsing --------------------------------------------------

    /// <summary>
    /// Tokenises an attribute string into <c>(Key, Value)</c> pairs. A <c>KEY=VALUE</c> pair has a
    /// non-null key; a bare token (positional value or flag) has a null key. Values may be quoted
    /// with <c>"</c> or <c>'</c>, or be a bare whitespace-delimited token.
    /// </summary>
    private static List<(string? Key, string Value)> ParseAttributes(string s)
    {
        var result = new List<(string?, string)>();
        var i = 0;
        var n = s.Length;

        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(s[i]))
            {
                i++;
            }

            if (i >= n)
            {
                break;
            }

            var quoted = s[i] == '"' || s[i] == '\'';
            var token = ReadToken(s, ref i, stopAtEquals: !quoted);

            if (!quoted && i < n && s[i] == '=')
            {
                i++; // consume '='
                var value = ReadToken(s, ref i, stopAtEquals: false);
                result.Add((token, value));
            }
            else
            {
                result.Add((null, token));
            }
        }

        return result;
    }

    private static string ReadToken(string s, ref int i, bool stopAtEquals)
    {
        var n = s.Length;
        if (i < n && (s[i] == '"' || s[i] == '\''))
        {
            var quote = s[i++];
            var start = i;
            while (i < n && s[i] != quote)
            {
                i++;
            }

            var value = s[start..i];
            if (i < n)
            {
                i++; // consume closing quote
            }

            return value;
        }

        var tokenStart = i;
        while (i < n && !char.IsWhiteSpace(s[i]) && !(stopAtEquals && s[i] == '='))
        {
            i++;
        }

        return s[tokenStart..i];
    }

    private static string? GetAttr(List<(string? Key, string Value)> attrs, string key)
    {
        foreach (var (k, v) in attrs)
        {
            if (k is not null && string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        return null;
    }

    private static bool HasFlag(List<(string? Key, string Value)> attrs, string flag)
    {
        foreach (var (k, v) in attrs)
        {
            if (k is not null && string.Equals(k, flag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (k is null && string.Equals(v, flag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the <paramref name="index"/>-th positional (unkeyed) value, skipping the PROMPT flag.</summary>
    private static string? Positional(List<(string? Key, string Value)> attrs, int index)
    {
        var seen = 0;
        foreach (var (k, v) in attrs)
        {
            if (k is not null || string.Equals(v, "PROMPT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen == index)
            {
                return v;
            }

            seen++;
        }

        return null;
    }
}
