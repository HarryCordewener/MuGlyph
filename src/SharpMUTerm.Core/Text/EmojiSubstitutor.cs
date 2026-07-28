using System.Text;

namespace SharpMUTerm.Core.Text;

/// <summary>
/// Substitutes text emoticons (<c>:)</c> → 🙂) and <c>:shortcode:</c> names (<c>:fire:</c> → 🔥)
/// with emoji, BeipMU-style. Emoticons are only replaced when flanked by whitespace or line
/// edges so they don't fire inside words or URLs (e.g. <c>http://</c>).
/// </summary>
public sealed class EmojiSubstitutor
{
    private static readonly IReadOnlyDictionary<string, string> DefaultEmoticons = new Dictionary<string, string>
    {
        [":)"] = "🙂", [":-)"] = "🙂", [":("] = "🙁", [":-("] = "🙁",
        [";)"] = "😉", [";-)"] = "😉", [":D"] = "😃", [":-D"] = "😃",
        [":P"] = "😛", [":-P"] = "😛", [":p"] = "😛", [":o"] = "😮", [":O"] = "😮",
        [":'("] = "😢", [":|"] = "😐", ["<3"] = "❤️", [":*"] = "😘",
        ["8)"] = "😎", ["B)"] = "😎", [">:("] = "😠", [":/"] = "😕",
    };

    private static readonly IReadOnlyDictionary<string, string> DefaultShortcodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["smile"] = "😄", ["grin"] = "😁", ["laugh"] = "😆", ["wink"] = "😉",
        ["heart"] = "❤️", ["fire"] = "🔥", ["star"] = "⭐", ["check"] = "✅",
        ["cross"] = "❌", ["thumbsup"] = "👍", ["thumbsdown"] = "👎", ["skull"] = "💀",
        ["sword"] = "⚔️", ["shield"] = "🛡️", ["dragon"] = "🐉", ["sparkles"] = "✨",
        ["wave"] = "👋", ["eyes"] = "👀", ["thinking"] = "🤔", ["tada"] = "🎉",
    };

    private readonly IReadOnlyDictionary<string, string> _emoticons;
    private readonly IReadOnlyDictionary<string, string> _shortcodes;

    public EmojiSubstitutor(
        bool emoticons = true,
        bool shortcodes = true,
        IReadOnlyDictionary<string, string>? extraShortcodes = null)
    {
        EmoticonsEnabled = emoticons;
        ShortcodesEnabled = shortcodes;
        _emoticons = DefaultEmoticons;

        if (extraShortcodes is null || extraShortcodes.Count == 0)
        {
            _shortcodes = DefaultShortcodes;
        }
        else
        {
            var merged = new Dictionary<string, string>(DefaultShortcodes, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in extraShortcodes)
            {
                merged[key] = value;
            }

            _shortcodes = merged;
        }
    }

    public bool EmoticonsEnabled { get; }

    public bool ShortcodesEnabled { get; }

    /// <summary>
    /// Substitutes emoji across a whole <see cref="StyledLine"/>, using the full line for word-boundary
    /// detection (so a token split across span seams is judged correctly) while preserving each
    /// character's style and interaction. Returns the same line when nothing changes.
    /// </summary>
    public StyledLine ApplyToLine(StyledLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.IsEmpty || (!EmoticonsEnabled && !ShortcodesEnabled))
        {
            return line;
        }

        var cells = new List<Cell>(line.Length);
        foreach (var span in line.Spans)
        {
            foreach (var ch in span.Text)
            {
                cells.Add(new Cell(ch.ToString(), span.Style, span.Interaction));
            }
        }

        var changed = false;
        if (ShortcodesEnabled)
        {
            changed |= ReplaceShortcodeCells(cells);
        }

        if (EmoticonsEnabled)
        {
            changed |= ReplaceEmoticonCells(cells);
        }

        return changed ? Coalesce(cells) : line;
    }

    private bool ReplaceShortcodeCells(List<Cell> cells)
    {
        var changed = false;
        for (var i = 0; i < cells.Count; i++)
        {
            if (cells[i].Text != ":")
            {
                continue;
            }

            var end = -1;
            for (var j = i + 1; j < cells.Count && j <= i + 33; j++)
            {
                if (cells[j].Text == ":")
                {
                    end = j;
                    break;
                }
            }

            if (end <= i + 1)
            {
                continue;
            }

            var name = string.Concat(cells.GetRange(i + 1, end - i - 1).Select(c => c.Text));
            if (IsShortcodeName(name) && _shortcodes.TryGetValue(name, out var emoji))
            {
                var style = cells[i].Style;
                cells.RemoveRange(i, end - i + 1);
                cells.Insert(i, new Cell(emoji, style, null));
                changed = true;
            }
        }

        return changed;
    }

    private bool ReplaceEmoticonCells(List<Cell> cells)
    {
        var changed = false;
        for (var i = 0; i < cells.Count; i++)
        {
            var atBoundary = i == 0 || IsWhitespaceCell(cells[i - 1]);
            if (!atBoundary)
            {
                continue;
            }

            foreach (var (token, emoji) in _emoticons)
            {
                if (i + token.Length > cells.Count)
                {
                    continue;
                }

                var matches = true;
                for (var k = 0; k < token.Length; k++)
                {
                    if (cells[i + k].Text.Length != 1 || cells[i + k].Text[0] != token[k])
                    {
                        matches = false;
                        break;
                    }
                }

                if (!matches)
                {
                    continue;
                }

                var after = i + token.Length;
                if (after != cells.Count && !IsWhitespaceCell(cells[after]))
                {
                    continue;
                }

                var style = cells[i].Style;
                cells.RemoveRange(i, token.Length);
                cells.Insert(i, new Cell(emoji, style, null));
                changed = true;
                break;
            }
        }

        return changed;
    }

    private static bool IsWhitespaceCell(Cell cell) => cell.Text.Length == 1 && char.IsWhiteSpace(cell.Text[0]);

    private static StyledLine Coalesce(List<Cell> cells)
    {
        if (cells.Count == 0)
        {
            return StyledLine.Empty;
        }

        var spans = new List<StyledSpan>();
        var sb = new System.Text.StringBuilder(cells[0].Text);
        var style = cells[0].Style;
        var link = cells[0].Link;
        for (var i = 1; i < cells.Count; i++)
        {
            if (cells[i].Style.Equals(style) && Equals(cells[i].Link, link))
            {
                sb.Append(cells[i].Text);
            }
            else
            {
                spans.Add(new StyledSpan(sb.ToString(), style, link));
                sb.Clear();
                sb.Append(cells[i].Text);
                style = cells[i].Style;
                link = cells[i].Link;
            }
        }

        spans.Add(new StyledSpan(sb.ToString(), style, link));
        return new StyledLine(spans);
    }

    private readonly record struct Cell(string Text, TextStyle Style, SpanInteraction? Link);

    /// <summary>Returns <paramref name="text"/> with emoticons and shortcodes replaced by emoji.</summary>
    public string Apply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return text;
        }

        var result = text;
        if (ShortcodesEnabled)
        {
            result = ReplaceShortcodes(result);
        }

        if (EmoticonsEnabled)
        {
            result = ReplaceEmoticons(result);
        }

        return result;
    }

    private string ReplaceShortcodes(string text)
    {
        if (text.IndexOf(':') < 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == ':')
            {
                var end = text.IndexOf(':', i + 1);
                if (end > i + 1)
                {
                    var name = text[(i + 1)..end];
                    if (IsShortcodeName(name) && _shortcodes.TryGetValue(name, out var emoji))
                    {
                        sb.Append(emoji);
                        i = end + 1;
                        continue;
                    }
                }
            }

            sb.Append(text[i]);
            i++;
        }

        return sb.ToString();
    }

    private static bool IsShortcodeName(string name) =>
        name.Length is > 0 and <= 32 && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '+' or '-');

    private string ReplaceEmoticons(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var matched = false;

            // Only attempt a match at a token boundary (start, or after whitespace).
            if (i == 0 || char.IsWhiteSpace(text[i - 1]))
            {
                foreach (var (token, emoji) in _emoticons)
                {
                    if (i + token.Length <= text.Length &&
                        string.CompareOrdinal(text, i, token, 0, token.Length) == 0 &&
                        (i + token.Length == text.Length || char.IsWhiteSpace(text[i + token.Length])))
                    {
                        sb.Append(emoji);
                        i += token.Length;
                        matched = true;
                        break;
                    }
                }
            }

            if (!matched)
            {
                sb.Append(text[i]);
                i++;
            }
        }

        return sb.ToString();
    }
}
