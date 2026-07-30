using System.Text;

namespace SharpMUTerm.Core.Telnet.Mssp;

/// <summary>
/// Reads MSSP subnegotiations straight off the inbound byte stream, incrementally and losslessly.
/// <para>
/// <b>Why this exists rather than the library's own reader.</b> TelnetNegotiationCore negotiates MSSP
/// and hands back a <c>MSSPConfig</c>, but that model cannot represent what MSSP sends: every
/// <c>MSSP_VAL</c> under one variable is concatenated into a single buffer with no separator before
/// anything sees it. <c>PORT</c> <c>80</c> <c>23</c> <c>4201</c> becomes the integer <c>80234201</c>,
/// and <c>REFERRAL</c> — which is nothing but an array — becomes a run-together string that then
/// fails to bind to its list-typed property and is discarded. The array is destroyed inside the
/// library, so no amount of post-processing recovers it, and <c>REFERRAL</c> is precisely the
/// variable MSSP exists to publish to crawlers. Boolean variables and every variable outside the
/// library's model (including the official <c>CHARSET</c>) are dropped as well. This is an upstream
/// bug and a good upstream PR; until then a crawler needs the bytes.
/// </para>
/// <para>
/// <b>Scope.</b> This parses; it does not negotiate. The option handshake stays with
/// TelnetNegotiationCore — which is what makes the server send the subnegotiation in the first place —
/// and this reads the payload it sends. There is no second telnet stack here: the only telnet framing
/// this understands is enough to find <c>IAC SB MSSP … IAC SE</c> in a stream and skip everything
/// else, including other options' subnegotiations, without being fooled by their payload bytes.
/// </para>
/// <para>
/// <b>One limitation, deliberately.</b> It reads the bytes as they arrive from the transport, so if
/// MCCP compression is already running when the payload arrives it sees compressed bytes and finds
/// nothing. <see cref="TelnetSession"/> covers that case by falling back to the library's own
/// (degraded) report, marked <see cref="MsspSource.Interpreter"/>. In practice MSSP is sent during the
/// opening handshake, before compression starts.
/// </para>
/// </summary>
public sealed class MsspSubnegotiationParser
{
    private const byte Iac = 255;
    private const byte Se = 240;
    private const byte Sb = 250;
    private const byte Will = 251;
    private const byte Dont = 254;
    private const byte MsspOption = 70;
    private const byte MsspVar = 1;
    private const byte MsspVal = 2;

    /// <summary>
    /// The default ceiling on one subnegotiation's payload. A legitimate MSSP report is a few hundred
    /// bytes; a referral list from an enthusiastic server might reach a few thousand. 64 KiB is far
    /// above anything real and far below anything that could exhaust a crawler probing many servers at
    /// once — a remote server must not be able to decide how much memory we spend on it.
    /// </summary>
    public const int DefaultMaxPayloadBytes = 64 * 1024;

    private enum Scan
    {
        Data,
        AfterIac,
        SubnegotiationOption,
        MsspPayload,
        MsspPayloadAfterIac,
        OtherSubnegotiation,
        OtherSubnegotiationAfterIac,
        SkipOptionByte,
    }

    private readonly int _maxPayloadBytes;
    private readonly List<byte> _field = [];

    private Scan _state = Scan.Data;
    private MsspData.Builder? _builder;
    private string? _variable;
    private bool _fieldIsValue;
    private bool _overflowed;

    public MsspSubnegotiationParser(Encoding? encoding = null, int maxPayloadBytes = DefaultMaxPayloadBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadBytes, 64);
        Encoding = encoding ?? Encoding.UTF8;
        _maxPayloadBytes = maxPayloadBytes;
    }

    /// <summary>
    /// The encoding variable names and values are decoded with. Read at the moment a field completes,
    /// not when the parser was built, so a CHARSET negotiation that settles mid-stream applies.
    /// </summary>
    public Encoding Encoding { get; set; }

    /// <summary>
    /// True when a payload was abandoned for exceeding <see cref="DefaultMaxPayloadBytes"/>. Sticky for
    /// the life of the parser, so a caller that only checks at the end still learns the report it got
    /// (or did not get) was truncated rather than simply absent.
    /// </summary>
    public bool Truncated => _overflowed;

    /// <summary>
    /// Feeds a chunk of inbound bytes and returns every MSSP report completed by it — normally none,
    /// once in the connection's lifetime exactly one. State carries across calls, so a payload split
    /// over any number of reads parses the same as one that arrives whole.
    /// </summary>
    public IReadOnlyList<MsspData> Consume(ReadOnlySpan<byte> bytes)
    {
        List<MsspData>? completed = null;

        foreach (var b in bytes)
        {
            switch (_state)
            {
                case Scan.Data:
                    if (b == Iac)
                    {
                        _state = Scan.AfterIac;
                    }

                    break;

                case Scan.AfterIac:
                    // IAC IAC in the data stream is an escaped 0xFF, not a command; either way there is
                    // nothing here to collect, so both land back in Data.
                    _state = b switch
                    {
                        Sb => Scan.SubnegotiationOption,
                        >= Will and <= Dont => Scan.SkipOptionByte,
                        _ => Scan.Data,
                    };
                    break;

                case Scan.SkipOptionByte:
                    _state = Scan.Data;
                    break;

                case Scan.SubnegotiationOption:
                    if (b == MsspOption)
                    {
                        BeginMssp();
                        _state = Scan.MsspPayload;
                    }
                    else
                    {
                        // Some other option's subnegotiation. It must still be scanned to its IAC SE,
                        // with IAC IAC honoured, or a GMCP payload containing 0xFF 0xFA 0x46 would look
                        // like the start of an MSSP report.
                        _state = Scan.OtherSubnegotiation;
                    }

                    break;

                case Scan.OtherSubnegotiation:
                    _state = b == Iac ? Scan.OtherSubnegotiationAfterIac : Scan.OtherSubnegotiation;
                    break;

                case Scan.OtherSubnegotiationAfterIac:
                    _state = b == Iac ? Scan.OtherSubnegotiation // escaped 0xFF inside the payload
                        : b == Se ? Scan.Data
                        : Scan.OtherSubnegotiation;
                    break;

                case Scan.MsspPayload:
                    if (b == Iac)
                    {
                        _state = Scan.MsspPayloadAfterIac;
                    }
                    else
                    {
                        Payload(b);
                    }

                    break;

                case Scan.MsspPayloadAfterIac:
                    if (b == Se)
                    {
                        if (Finish() is { } report)
                        {
                            (completed ??= []).Add(report);
                        }

                        _state = Scan.Data;
                    }
                    else
                    {
                        // IAC IAC is a literal 0xFF in the payload. Any other command byte here is a
                        // protocol violation; treating it as payload keeps the report rather than
                        // discarding a whole server's data over one stray byte.
                        Payload(b);
                        _state = Scan.MsspPayload;
                    }

                    break;
            }
        }

        return completed ?? [];
    }

    private void BeginMssp()
    {
        _builder = new MsspData.Builder();
        _field.Clear();
        _variable = null;
        _fieldIsValue = false;
    }

    private void Payload(byte b)
    {
        switch (b)
        {
            case MsspVar:
                FlushField();
                _fieldIsValue = false;
                _variable = null;
                break;

            case MsspVal:
                FlushField();
                _fieldIsValue = true;
                break;

            default:
                if (_field.Count >= _maxPayloadBytes)
                {
                    _overflowed = true;
                    return;
                }

                _field.Add(b);
                break;
        }
    }

    private void FlushField()
    {
        if (_builder is null)
        {
            return;
        }

        var text = _field.Count == 0 ? string.Empty : Encoding.GetString(_field.ToArray());
        _field.Clear();

        if (_fieldIsValue)
        {
            // A value with no variable before it is malformed; there is nothing to attach it to.
            if (_variable is not null)
            {
                _builder.Add(_variable, text);
            }

            return;
        }

        if (text.Length == 0)
        {
            return;
        }

        // A variable name. Recorded before any value arrives, so a variable a server sends with no
        // MSSP_VAL at all is still reported as present-but-empty rather than vanishing.
        _variable = text;
        _builder.Declare(text);
    }

    private MsspData? Finish()
    {
        FlushField();
        var report = _builder?.Build();
        _builder = null;
        _variable = null;
        _fieldIsValue = false;
        _field.Clear();
        return report;
    }
}
