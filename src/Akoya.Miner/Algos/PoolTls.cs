// Shared TLS wrapper for the stratum-based algo clients (CSD, GR, RX). Pool TLS is
// encryption-in-transit, not identity verification — mining pools routinely
// serve self-signed / name-mismatched certs — so by default any cert is
// accepted and its SHA-256 logged. Mirrors Akoya.Pool.StratumSession's policy.

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace Akoya.Miner.Algos;

internal static class PoolTls
{
    /// <summary>Connects a TcpClient to <paramref name="host"/> and <paramref name="port"/>,
    /// stripping trailing slashes/paths and explicitly resolving DNS to IPv4/IPv6 IP addresses
    /// to avoid WSANO_DATA ("The requested name is valid, but no data of the requested type was found") errors.</summary>
    public static async Task ConnectTcpAsync(TcpClient tcp, string host, int port, CancellationToken ct)
    {
        string cleanHost = host.Trim();
        int slash = cleanHost.IndexOf('/');
        if (slash >= 0) cleanHost = cleanHost[..slash];

        if (IPAddress.TryParse(cleanHost, out var ip))
        {
            await tcp.ConnectAsync(ip, port, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var ips = await Dns.GetHostAddressesAsync(cleanHost, ct).ConfigureAwait(false);
            var v4 = ips.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            var targetIp = v4 ?? ips.FirstOrDefault();
            if (targetIp is not null)
            {
                await tcp.ConnectAsync(targetIp, port, ct).ConfigureAwait(false);
                return;
            }
        }
        catch
        {
            // Fallback to direct host connect if explicit DNS resolution throws
        }

        await tcp.ConnectAsync(cleanHost, port, ct).ConfigureAwait(false);
    }

    /// <summary>Returns the plaintext stream, or an authenticated SslStream when
    /// <paramref name="useTls"/> is set. Handshake is bounded to 15s so a
    /// plain-TCP port dialed as TLS fails fast into the caller's reconnect loop.</summary>
    public static async Task<Stream> WrapAsync(TcpClient tcp, string host, bool useTls, ILogger log, CancellationToken ct)
    {
        Stream stream = tcp.GetStream();
        if (!useTls) return stream;

        string cleanHost = host.Trim();
        int slash = cleanHost.IndexOf('/');
        if (slash >= 0) cleanHost = cleanHost[..slash];

#pragma warning disable CA5359 // Stratum / RPC mining pools frequently serve self-signed TLS certificates for transport encryption
        var ssl = new SslStream(stream, leaveInnerStreamOpen: false,
            (_, cert, _, _) =>
            {
                if (cert is not null)
                {
                    log.LogDebug("pool: TLS cert SHA-256 {Thumb}",
                        Convert.ToHexStringLower(SHA256.HashData(cert.GetRawCertData())));
                }
                return true; // encryption-in-transit; pools serve self-signed certs
            });
#pragma warning restore CA5359
        var opts = new SslClientAuthenticationOptions
        {
            TargetHost = cleanHost,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        };
        using var hsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hsCts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await ssl.AuthenticateAsClientAsync(opts, hsCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"pool: TLS handshake to {cleanHost} timed out after 15s — the port may be plain-TCP only (try without TLS).");
        }
        log.LogInformation("pool: TLS connected (proto={Proto})", ssl.SslProtocol);
        return ssl;
    }
}
