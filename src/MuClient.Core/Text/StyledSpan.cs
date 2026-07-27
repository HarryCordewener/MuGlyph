namespace MuClient.Core.Text;

/// <summary>A run of text sharing a single <see cref="TextStyle"/>, optionally interactive.</summary>
public readonly struct StyledSpan : IEquatable<StyledSpan>
{
    public StyledSpan(string text, TextStyle style, SpanInteraction? interaction = null)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Style = style;
        Interaction = interaction;
    }

    public string Text { get; }

    public TextStyle Style { get; }

    /// <summary>Optional click behaviour (a command to send or a link to open); null for plain text.</summary>
    public SpanInteraction? Interaction { get; }

    public int Length => Text.Length;

    public bool IsInteractive => Interaction is not null;

    public bool Equals(StyledSpan other) =>
        Text == other.Text && Style.Equals(other.Style) && Equals(Interaction, other.Interaction);

    public override bool Equals(object? obj) => obj is StyledSpan other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Text, Style, Interaction);

    public override string ToString() => Text;
}
