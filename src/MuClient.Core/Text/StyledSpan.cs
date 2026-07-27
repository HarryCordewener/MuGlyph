namespace MuClient.Core.Text;

/// <summary>A run of text sharing a single <see cref="TextStyle"/>.</summary>
public readonly struct StyledSpan : IEquatable<StyledSpan>
{
    public StyledSpan(string text, TextStyle style)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Style = style;
    }

    public string Text { get; }

    public TextStyle Style { get; }

    public int Length => Text.Length;

    public bool Equals(StyledSpan other) => Text == other.Text && Style.Equals(other.Style);

    public override bool Equals(object? obj) => obj is StyledSpan other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Text, Style);

    public override string ToString() => Text;
}
