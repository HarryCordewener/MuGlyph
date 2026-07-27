using System.Text;

namespace MuClient.Core.Text;

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
