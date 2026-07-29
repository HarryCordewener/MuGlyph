using System.Text;

namespace SharpMUTerm.Core.Text;

/// <summary>
/// The on-disk encoding of a <see cref="StyledLine"/> used by <see cref="IScrollbackSpill"/>:
/// a compact, self-describing binary payload carrying every span's text, style, and interaction.
/// <para>
/// Deliberately <em>not</em> the UI's Spectre-style markup. Markup is a Tui concern
/// (<c>MarkupFormatter</c>) and Core must not depend on it, but it is also the wrong format on the
/// merits: it cannot distinguish a palette index from the RGB a theme resolves it to, it has to
/// escape <c>[</c>, it loses a span's <see cref="SpanInteraction"/> hint and prompt-only flag, and
/// re-parsing it costs more than decoding this does.
/// </para>
/// </summary>
/// <remarks>
/// Payload layout (little-endian; every <c>string</c> is <see cref="BinaryWriter.Write(string)"/>'s
/// 7-bit-length-prefixed UTF-8, so text needs no escaping and may contain any character including
/// control bytes and newlines):
/// <code>
/// byte    flags            bit 0: a RuleColor follows
/// colour  ruleColor        (present only when bit 0 is set)
/// 7bit    spanCount
/// spanCount times:
///   string  text
///   colour  foreground
///   colour  background
///   uint16  attributes     (TextAttributes)
///   byte    hasInteraction
///   present only when hasInteraction != 0:
///     byte    kind         (InteractionKind)
///     string  target
///     byte    hasHint
///     string  hint         (present only when hasHint != 0)
///     byte    promptOnly
///
/// colour := byte kind (TerminalColorKind) then, per kind:
///           Default -> nothing; Indexed -> byte index; Rgb -> byte r, g, b
/// </code>
/// Framing (length prefix and CRC) belongs to the store, not here — see
/// <see cref="FileScrollbackSpill"/>.
/// </remarks>
public static class StyledLineCodec
{
    private const byte FlagHasRuleColor = 1 << 0;

    /// <summary>
    /// The encoding used for every string field. Replacement (not exception) fallback, so an
    /// unpaired surrogate — which nothing in the inbound pipeline can produce, since text is decoded
    /// from bytes through an <see cref="Encoding"/> — degrades to U+FFFD instead of failing a write.
    /// </summary>
    public static Encoding TextEncoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Writes <paramref name="line"/>'s payload (no framing) to <paramref name="writer"/>.</summary>
    public static void Write(BinaryWriter writer, StyledLine line)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(line);

        var flags = (byte)(line.RuleColor is null ? 0 : FlagHasRuleColor);
        writer.Write(flags);
        if (line.RuleColor is { } rule)
        {
            WriteColor(writer, rule);
        }

        var spans = line.Spans;
        writer.Write7BitEncodedInt(spans.Count);
        foreach (var span in spans)
        {
            writer.Write(span.Text);
            WriteColor(writer, span.Style.Foreground);
            WriteColor(writer, span.Style.Background);
            writer.Write((ushort)span.Style.Attributes);

            if (span.Interaction is not { } interaction)
            {
                writer.Write((byte)0);
                continue;
            }

            writer.Write((byte)1);
            writer.Write((byte)interaction.Kind);
            writer.Write(interaction.Target);
            writer.Write((byte)(interaction.Hint is null ? 0 : 1));
            if (interaction.Hint is not null)
            {
                writer.Write(interaction.Hint);
            }

            writer.Write((byte)(interaction.PromptOnly ? 1 : 0));
        }
    }

    /// <summary>
    /// Reads a payload written by <see cref="Write"/>. Throws on malformed input — callers verify
    /// the record's checksum first and treat a throw as a corrupt record.
    /// </summary>
    public static StyledLine Read(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var flags = reader.ReadByte();
        TerminalColor? rule = (flags & FlagHasRuleColor) != 0 ? ReadColor(reader) : null;

        var spanCount = reader.Read7BitEncodedInt();
        if (spanCount < 0)
        {
            throw new InvalidDataException($"Negative span count ({spanCount}) in a scrollback record.");
        }

        if (spanCount == 0)
        {
            return rule is null ? StyledLine.Empty : StyledLine.Empty.WithRule(rule.Value);
        }

        var spans = new StyledSpan[spanCount];
        for (var i = 0; i < spanCount; i++)
        {
            var text = reader.ReadString();
            var foreground = ReadColor(reader);
            var background = ReadColor(reader);
            var attributes = (TextAttributes)reader.ReadUInt16();
            var style = new TextStyle(foreground, background, attributes);

            SpanInteraction? interaction = null;
            if (reader.ReadByte() != 0)
            {
                var kind = (InteractionKind)reader.ReadByte();
                var target = reader.ReadString();
                var hint = reader.ReadByte() != 0 ? reader.ReadString() : null;
                var promptOnly = reader.ReadByte() != 0;
                interaction = new SpanInteraction(kind, target, hint, promptOnly);
            }

            spans[i] = new StyledSpan(text, style, interaction);
        }

        return new StyledLine(spans, rule);
    }

    /// <summary>Encodes one line's payload to a fresh array (convenience for tests and callers off the hot path).</summary>
    public static byte[] Encode(StyledLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        using var stream = new MemoryStream(128);
        using (var writer = new BinaryWriter(stream, TextEncoding, leaveOpen: true))
        {
            Write(writer, line);
        }

        return stream.ToArray();
    }

    /// <summary>Decodes a payload produced by <see cref="Encode"/>.</summary>
    public static StyledLine Decode(byte[] payload, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var stream = new MemoryStream(payload, offset, count, writable: false);
        using var reader = new BinaryReader(stream, TextEncoding, leaveOpen: true);
        return Read(reader);
    }

    /// <inheritdoc cref="Decode(byte[],int,int)"/>
    public static StyledLine Decode(byte[] payload) => Decode(payload, 0, payload?.Length ?? 0);

    private static void WriteColor(BinaryWriter writer, TerminalColor color)
    {
        writer.Write((byte)color.Kind);
        switch (color.Kind)
        {
            case TerminalColorKind.Indexed:
                writer.Write(color.Index);
                break;
            case TerminalColorKind.Rgb:
                writer.Write(color.R);
                writer.Write(color.G);
                writer.Write(color.B);
                break;
        }
    }

    private static TerminalColor ReadColor(BinaryReader reader) => (TerminalColorKind)reader.ReadByte() switch
    {
        TerminalColorKind.Default => TerminalColor.Default,
        TerminalColorKind.Indexed => TerminalColor.FromIndex(reader.ReadByte()),
        TerminalColorKind.Rgb => TerminalColor.FromRgb(reader.ReadByte(), reader.ReadByte(), reader.ReadByte()),
        var kind => throw new InvalidDataException($"Unknown colour kind {(int)kind} in a scrollback record."),
    };
}
