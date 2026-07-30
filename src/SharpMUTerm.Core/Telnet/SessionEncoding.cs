using System.Text;

namespace SharpMUTerm.Core.Telnet;

/// <summary>Where a session's text encoding came from — the difference between a fact and an assumption.</summary>
public enum EncodingSource
{
    /// <summary>
    /// Nothing negotiated it and nothing pinned it: the head of the stated preference order, used
    /// because bytes have to be turned into text somehow. Many MU* servers never implement CHARSET
    /// (RFC 2066), so this is a normal state, not an error one.
    /// </summary>
    Assumed,

    /// <summary>CHARSET negotiation (RFC 2066) settled on this. The server agreed to it.</summary>
    Negotiated,

    /// <summary>
    /// The world pins this encoding, so negotiation cannot move it. It is still offered at the head of
    /// the preference order, so a cooperative server agrees rather than diverging.
    /// </summary>
    Override,
}

/// <summary>
/// The encoding a session is decoding and encoding with <em>now</em>, and why. Both halves matter: a
/// user chasing mojibake needs to know whether the server chose this or they did.
/// </summary>
/// <param name="Encoding">The encoding in force.</param>
/// <param name="Source">How it came to be in force.</param>
public readonly record struct SessionEncoding(Encoding Encoding, EncodingSource Source)
{
    /// <summary>The IANA name, e.g. <c>utf-8</c>.</summary>
    public string Name => Encoding.WebName;

    /// <summary>
    /// A short human label for a status surface: the name alone when the server agreed to it, and the
    /// name plus one qualifying word when it did not. Unqualified therefore means "negotiated", which
    /// is the common case and the one worth spending the fewest cells on.
    /// </summary>
    public string Label => Source switch
    {
        EncodingSource.Negotiated => Name,
        EncodingSource.Override => $"{Name} forced",
        _ => $"{Name} assumed",
    };
}

/// <summary>Carries a change of the encoding in force.</summary>
public sealed class SessionEncodingEventArgs(SessionEncoding encoding) : EventArgs
{
    public SessionEncoding Encoding { get; } = encoding;
}
