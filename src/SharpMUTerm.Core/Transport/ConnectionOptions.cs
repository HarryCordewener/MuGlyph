namespace SharpMUTerm.Core.Transport;

/// <summary>Immutable connection parameters for a single MU* world.</summary>
public sealed class ConnectionOptions
{
    /// <summary>Hostname or IP literal (IPv4 or IPv6) of the server.</summary>
    public required string Host { get; init; }

    /// <summary>TCP port.</summary>
    public required int Port { get; init; }

    /// <summary>When true, wrap the socket in TLS via <see cref="System.Net.Security.SslStream"/>.</summary>
    public bool UseTls { get; init; }

    /// <summary>
    /// When true, accept server certificates that fail validation (self-signed certs are
    /// common on hobbyist MU* servers). Off by default.
    /// </summary>
    public bool AllowInvalidCertificates { get; init; }

    /// <summary>SNI / certificate target host. Defaults to <see cref="Host"/> when null.</summary>
    public string? TlsTargetHost { get; init; }

    /// <summary>Socket connect timeout. Defaults to 30 seconds.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public override string ToString() => $"{(UseTls ? "telnets" : "telnet")}://{Host}:{Port}";
}
