// Pool URL parsing, shared by --pool (GPU side) and --pool-cpu (the CPU algo's
// own pool when dual-mining), so both accept exactly the same schemes.
//
// Lifted out of Program.cs's top-level statements: as a local function it could
// not be unit-tested, and the URL splitting — bracketed IPv6, %zone ids, the
// last-colon rule — is precisely the kind of pure logic that wants tests.

namespace Akoya.Miner.Config;

internal static class PoolUrl
{
    /// <summary>Split a pool URL into its parts. <c>Tls</c> is null when the
    /// scheme said nothing about it; <c>Port</c> is null when the URL carried
    /// none (the caller supplies the algo's default).</summary>
    public static (string Host, string? Port, bool IsStratum, bool? Tls) Parse(string val)
    {
        ArgumentNullException.ThrowIfNull(val);

        bool isStratum = false;
        bool? tls = null;

        if (val.StartsWith("stratum+tcp://", StringComparison.OrdinalIgnoreCase))
        {
            val = val["stratum+tcp://".Length..]; isStratum = true; tls = false;
        }
        else if (val.StartsWith("stratum+ssl://", StringComparison.OrdinalIgnoreCase))
        {
            val = val["stratum+ssl://".Length..]; isStratum = true; tls = true;
        }
        else if (val.StartsWith("stratum+tls://", StringComparison.OrdinalIgnoreCase))
        {
            val = val["stratum+tls://".Length..]; isStratum = true; tls = true;
        }
        else if (val.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            val = val["tcp://".Length..]; isStratum = true; tls = false;
        }
        else if (val.StartsWith("ssl://", StringComparison.OrdinalIgnoreCase))
        {
            val = val["ssl://".Length..]; isStratum = true; tls = true;
        }
        else if (val.StartsWith("stratum://", StringComparison.OrdinalIgnoreCase))
        {
            val = val["stratum://".Length..]; isStratum = true;
        }
        else if (val.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            val = val["https://".Length..];
        }
        else if (val.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            val = val["http://".Length..];
        }

        // Split host:port. Must handle bracketed IPv6 literals —
        // [fe80::1%14]:3335 — whose address is full of colons, so a naive
        // Split(':') shreds it. Strip any trailing /path first.
        int slash = val.IndexOf('/');
        if (slash >= 0) val = val[..slash];

        string host;
        string? portStr = null;
        if (val.StartsWith('['))
        {
            // [addr]:port — addr may carry a %zone id (link-local).
            int close = val.IndexOf(']');
            if (close > 0)
            {
                host = val[1..close];
                var rest = val[(close + 1)..];
                if (rest.StartsWith(':') && rest.Length > 1) portStr = rest[1..];
            }
            else
            {
                host = val; // malformed bracket — pass through, let connect fail clearly
            }
        }
        else
        {
            // host:port — split on the LAST colon so hostnames / IPv4 work.
            // A bare (unbracketed) IPv6 literal isn't supported here; wrap it
            // in [ ] (standard URL form).
            int colon = val.LastIndexOf(':');
            if (colon >= 0)
            {
                host = val[..colon];
                portStr = val[(colon + 1)..];
            }
            else
            {
                host = val;
            }
        }

        return (host, portStr, isStratum, tls);
    }
}
