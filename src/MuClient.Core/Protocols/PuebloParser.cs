using System.Text;
using MuClient.Core.Text;

namespace MuClient.Core.Protocols;

/// <summary>
/// Incremental, line-oriented parser for <b>Pueblo</b> markup — the HTML subset spoken by some
/// MU* servers (a predecessor and cousin of MXP). Feed it decoded text (any number of chunks);
/// it emits fully-terminated <see cref="StyledLine"/>s and retains the in-progress line, the
/// current <see cref="TextStyle"/>, and the open-tag stack across calls, so a tag
/// (<c>&lt;...&gt;</c>) or entity (<c>&amp;...;</c>) may span chunk boundaries.
///
/// Supported: the formatting tags (<c>B/STRONG</c>, <c>I/EM</c>, <c>U</c>, <c>STRIKE/S</c>),
/// <c>&lt;FONT COLOR BGCOLOR&gt;</c>, the Pueblo command/link anchor
/// (<c>&lt;A XCH_CMD&gt;</c> / <c>&lt;A HREF&gt;</c>, plus <c>&lt;SEND&gt;</c>), the break tags
/// (<c>BR</c>, <c>HR</c>, <c>P</c>), preformatted passthrough (<c>PRE</c>), graceful
/// <c>&lt;IMG&gt;</c> discard, and the common named/numeric entities. Unknown tags are consumed
/// and never leak into the output.
///
/// Out of scope: the Pueblo activation handshake (the <c>&lt;!-- --&gt;</c> /
/// "This world is Pueblo …" enabling exchange). This class parses the <i>markup</i> only;
/// deciding whether a world is Pueblo-enabled is a session concern handled elsewhere. ANSI/VT
/// escape sequences are handled upstream, so an ESC (0x1b) byte is passed through untouched into
/// the span text rather than being interpreted here.
/// </summary>
public sealed class PuebloParser : MuClient.Core.Text.ILineParser
{
    private const int MaxTagLength = 4096;
    private const int MaxEntityLength = 32;

    private enum State
    {
        Ground,
        Tag,
        Entity,
    }

    /// <summary>A saved rendition frame, pushed when an element opens and restored when it closes.</summary>
    private readonly struct Frame
    {
        public Frame(string name, TextStyle style, SpanInteraction? interaction)
        {
            Name = name;
            Style = style;
            Interaction = interaction;
        }

        public string Name { get; }

        public TextStyle Style { get; }

        public SpanInteraction? Interaction { get; }
    }

    private State _state = State.Ground;
    private TextStyle _current = TextStyle.Default;
    private SpanInteraction? _interaction;
    private readonly StringBuilder _run = new();
    private readonly List<StyledSpan> _lineSpans = new();
    private readonly StringBuilder _tag = new();
    private readonly StringBuilder _entity = new();
    private readonly List<Frame> _stack = new();

    /// <summary>The rendition state that will apply to the next printed character.</summary>
    public TextStyle CurrentStyle => _current;

    /// <summary>True when a partial line, tag, or entity is buffered.</summary>
    public bool HasPendingContent =>
        _run.Length > 0 || _lineSpans.Count > 0 || _state != State.Ground;

    /// <summary>Feeds a chunk of text, returning every line completed within it.</summary>
    public IReadOnlyList<StyledLine> Feed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Feed(text.AsSpan());
    }

    /// <summary>Feeds a chunk of text, returning every line completed within it.</summary>
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
    /// Returns the buffered partial line (e.g. a prompt not terminated by a break) and clears it,
    /// or <c>null</c> if nothing is buffered. Style and open-tag state are preserved.
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

    /// <summary>Resets all parser state, including the current style and open-tag stack.</summary>
    public void Reset()
    {
        _state = State.Ground;
        _current = TextStyle.Default;
        _interaction = null;
        _run.Clear();
        _lineSpans.Clear();
        _tag.Clear();
        _entity.Clear();
        _stack.Clear();
    }

    private void Process(char ch, ref List<StyledLine>? lines)
    {
        switch (_state)
        {
            case State.Ground:
                ProcessGround(ch, ref lines);
                break;

            case State.Tag:
                ProcessTag(ch, ref lines);
                break;

            case State.Entity:
                ProcessEntity(ch, ref lines);
                break;
        }
    }

    private void ProcessGround(char ch, ref List<StyledLine>? lines)
    {
        switch (ch)
        {
            case '<':
                _tag.Clear();
                _state = State.Tag;
                break;

            case '&':
                _entity.Clear();
                _state = State.Entity;
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

    private void ProcessTag(char ch, ref List<StyledLine>? lines)
    {
        switch (ch)
        {
            case '>':
                HandleTag(_tag.ToString(), ref lines);
                _tag.Clear();
                _state = State.Ground;
                break;

            case '<':
                // The previous '<' plus what we buffered was not a tag — emit it literally and
                // begin a fresh tag with this '<'.
                _run.Append('<').Append(_tag);
                _tag.Clear();
                break;

            case '\n':
                // A newline inside a "tag" means it never closed — treat it as literal text.
                _run.Append('<').Append(_tag);
                _tag.Clear();
                _state = State.Ground;
                CompleteLine(ref lines);
                break;

            default:
                if (_tag.Length >= MaxTagLength)
                {
                    // Runaway: give up on the tag and emit what we have as literal text.
                    _run.Append('<').Append(_tag);
                    _tag.Clear();
                    _state = State.Ground;
                    ProcessGround(ch, ref lines);
                }
                else
                {
                    _tag.Append(ch);
                }

                break;
        }
    }

    private void ProcessEntity(char ch, ref List<StyledLine>? lines)
    {
        // Entity names are alphanumeric, optionally prefixed with '#' (and 'x'/'X' for hex).
        if (ch == ';')
        {
            if (TryResolveEntity(_entity.ToString(), out var resolved))
            {
                _run.Append(resolved);
            }
            else
            {
                // Not a recognised entity — emit it verbatim so nothing is lost.
                _run.Append('&').Append(_entity).Append(';');
            }

            _entity.Clear();
            _state = State.Ground;
            return;
        }

        if (ch == '&')
        {
            // A second '&' means the first was a literal ampersand.
            _run.Append('&').Append(_entity);
            _entity.Clear();
            return;
        }

        if ((char.IsLetterOrDigit(ch) || ch == '#') && _entity.Length < MaxEntityLength)
        {
            _entity.Append(ch);
            return;
        }

        // Any other character (whitespace, '<', overflow, …) aborts the entity: emit the buffered
        // text literally, then reprocess this character from the Ground state.
        _run.Append('&').Append(_entity);
        _entity.Clear();
        _state = State.Ground;
        ProcessGround(ch, ref lines);
    }

    private void HandleTag(string raw, ref List<StyledLine>? lines)
    {
        var tag = PuebloTag.Parse(raw);
        if (tag.Name.Length == 0)
        {
            // Empty, comment (<!-- -->), or declaration (<!...>) — nothing to render.
            return;
        }

        if (tag.IsClose)
        {
            CloseTag(tag.Name);
            return;
        }

        switch (tag.Name)
        {
            case "b":
            case "strong":
                OpenAttribute(tag.Name, TextAttributes.Bold);
                break;
            case "i":
            case "em":
                OpenAttribute(tag.Name, TextAttributes.Italic);
                break;
            case "u":
                OpenAttribute(tag.Name, TextAttributes.Underline);
                break;
            case "strike":
            case "s":
                OpenAttribute(tag.Name, TextAttributes.Strikethrough);
                break;

            case "font":
                OpenFont(tag);
                break;

            case "a":
            case "send":
                OpenAnchor(tag);
                break;

            case "br":
                CompleteLine(ref lines);
                break;
            case "hr":
                // A rule renders as a break followed by a blank separating line.
                CompleteLine(ref lines);
                CompleteLine(ref lines);
                break;
            case "p":
                // Paragraph boundaries (open and close) act as a single line break.
                CompleteLine(ref lines);
                break;

            case "pre":
            case "img":
                // <PRE> content is already preformatted; <IMG> is handled by the graphics layer.
                // Both are consumed here so they never leak into the text.
                break;

            default:
                // Unknown tag — consumed and ignored.
                break;
        }
    }

    private void OpenAttribute(string name, TextAttributes attribute)
    {
        FlushRun();
        _stack.Add(new Frame(name, _current, _interaction));
        _current = _current.AddAttribute(attribute);
    }

    private void OpenFont(PuebloTag tag)
    {
        FlushRun();
        _stack.Add(new Frame("font", _current, _interaction));

        if (tag.TryGet("color", out var fg) && WebColors.TryParse(fg, out var fgColor))
        {
            _current = _current.WithForeground(fgColor);
        }

        if (tag.TryGet("bgcolor", out var bg) && WebColors.TryParse(bg, out var bgColor))
        {
            _current = _current.WithBackground(bgColor);
        }
    }

    private void OpenAnchor(PuebloTag tag)
    {
        FlushRun();
        _stack.Add(new Frame(tag.Name, _current, _interaction));

        tag.TryGet("xch_hint", out var hint);
        if (string.IsNullOrEmpty(hint))
        {
            tag.TryGet("title", out hint);
        }

        if (tag.TryGet("xch_cmd", out var cmd) && !string.IsNullOrEmpty(cmd))
        {
            var promptOnly = tag.TryGet("xch_mode", out var mode) &&
                             string.Equals(mode, "prompt", StringComparison.OrdinalIgnoreCase);
            _interaction = SpanInteraction.Command(cmd, NullIfEmpty(hint), promptOnly);
        }
        else if (tag.TryGet("href", out var href) && !string.IsNullOrEmpty(href))
        {
            // <SEND HREF> carries a command; <A HREF> without XCH_CMD is a hyperlink.
            _interaction = tag.Name == "send"
                ? SpanInteraction.Command(href, NullIfEmpty(hint))
                : SpanInteraction.Link(href, NullIfEmpty(hint));
        }

        // With neither XCH_CMD nor HREF the anchor carries no interaction; the frame still keeps
        // the stack balanced so its closer restores state cleanly.
    }

    private void CloseTag(string name)
    {
        // Pop down to (and including) the nearest matching open frame, restoring its saved state.
        // Unbalanced closers with no matching open frame are ignored.
        for (var i = _stack.Count - 1; i >= 0; i--)
        {
            if (_stack[i].Name != name && !IsAlias(_stack[i].Name, name))
            {
                continue;
            }

            FlushRun();
            _current = _stack[i].Style;
            _interaction = _stack[i].Interaction;
            _stack.RemoveRange(i, _stack.Count - i);
            return;
        }
    }

    /// <summary>True when two tag names denote the same rendition (e.g. <c>b</c>/<c>strong</c>).</summary>
    private static bool IsAlias(string open, string close) => (open, close) switch
    {
        ("b", "strong") or ("strong", "b") => true,
        ("i", "em") or ("em", "i") => true,
        ("strike", "s") or ("s", "strike") => true,
        _ => false,
    };

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

        _lineSpans.Add(new StyledSpan(_run.ToString(), _current, _interaction));
        _run.Clear();
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static bool TryResolveEntity(string name, out string value)
    {
        switch (name.ToLowerInvariant())
        {
            case "lt": value = "<"; return true;
            case "gt": value = ">"; return true;
            case "amp": value = "&"; return true;
            case "quot": value = "\""; return true;
            case "apos": value = "'"; return true;
            case "nbsp": value = " "; return true;
        }

        if (name.Length >= 2 && name[0] == '#')
        {
            var isHex = name[1] is 'x' or 'X';
            var digits = isHex ? name.AsSpan(2) : name.AsSpan(1);
            var style = isHex
                ? System.Globalization.NumberStyles.HexNumber
                : System.Globalization.NumberStyles.Integer;

            if (digits.Length > 0 &&
                int.TryParse(digits, style, null, out var code) &&
                code is > 0 and <= 0x10FFFF &&
                !(code >= 0xD800 && code <= 0xDFFF))
            {
                value = char.ConvertFromUtf32(code);
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    /// <summary>A parsed tag: its lower-cased name, whether it is a closer, and its attributes.</summary>
    private readonly struct PuebloTag
    {
        private readonly Dictionary<string, string>? _attributes;

        private PuebloTag(bool isClose, string name, Dictionary<string, string>? attributes)
        {
            IsClose = isClose;
            Name = name;
            _attributes = attributes;
        }

        public bool IsClose { get; }

        public string Name { get; }

        public bool TryGet(string key, out string value)
        {
            if (_attributes is not null && _attributes.TryGetValue(key, out var v))
            {
                value = v;
                return true;
            }

            value = string.Empty;
            return false;
        }

        /// <summary>Parses the raw text between <c>&lt;</c> and <c>&gt;</c> into name + attributes.</summary>
        public static PuebloTag Parse(string raw)
        {
            var i = 0;
            var len = raw.Length;

            while (i < len && char.IsWhiteSpace(raw[i]))
            {
                i++;
            }

            if (i >= len)
            {
                return new PuebloTag(false, string.Empty, null);
            }

            // Comments (<!-- ... -->) and declarations (<!...>) carry no renderable name.
            if (raw[i] == '!' || raw[i] == '?')
            {
                return new PuebloTag(false, string.Empty, null);
            }

            var isClose = false;
            if (raw[i] == '/')
            {
                isClose = true;
                i++;
            }

            var nameStart = i;
            while (i < len && !char.IsWhiteSpace(raw[i]) && raw[i] != '/' && raw[i] != '=')
            {
                i++;
            }

            var name = raw[nameStart..i].ToLowerInvariant();
            if (name.Length == 0)
            {
                return new PuebloTag(isClose, string.Empty, null);
            }

            if (isClose)
            {
                // Closers carry no meaningful attributes.
                return new PuebloTag(true, name, null);
            }

            Dictionary<string, string>? attributes = null;

            while (i < len)
            {
                while (i < len && (char.IsWhiteSpace(raw[i]) || raw[i] == '/'))
                {
                    i++;
                }

                if (i >= len)
                {
                    break;
                }

                var keyStart = i;
                while (i < len && !char.IsWhiteSpace(raw[i]) && raw[i] != '=' && raw[i] != '/')
                {
                    i++;
                }

                var key = raw[keyStart..i].ToLowerInvariant();

                // Skip whitespace before a possible '='.
                var j = i;
                while (j < len && char.IsWhiteSpace(raw[j]))
                {
                    j++;
                }

                var value = string.Empty;
                if (j < len && raw[j] == '=')
                {
                    i = j + 1;
                    while (i < len && char.IsWhiteSpace(raw[i]))
                    {
                        i++;
                    }

                    if (i < len && (raw[i] == '"' || raw[i] == '\''))
                    {
                        var quote = raw[i++];
                        var valStart = i;
                        while (i < len && raw[i] != quote)
                        {
                            i++;
                        }

                        value = raw[valStart..i];
                        if (i < len)
                        {
                            i++; // consume the closing quote
                        }
                    }
                    else
                    {
                        var valStart = i;
                        while (i < len && !char.IsWhiteSpace(raw[i]) && raw[i] != '/')
                        {
                            i++;
                        }

                        value = raw[valStart..i];
                    }
                }

                if (key.Length > 0)
                {
                    (attributes ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))[key] = value;
                }
            }

            return new PuebloTag(false, name, attributes);
        }
    }
}
