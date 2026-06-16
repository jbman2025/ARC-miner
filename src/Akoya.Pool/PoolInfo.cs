using System.Text.Json;

namespace Akoya.Pool;

/// <summary>
/// Confidence attached to a displayed <see cref="PoolInfo"/>. The miner MUST
/// show which level it is presenting (see docs/POOL-FEE-TRANSPARENCY.md §7).
/// Only <see cref="NotAdvertised"/> and <see cref="SelfReported"/> are produced
/// today; Signed / Registry levels are reserved for a later pass.
/// </summary>
public enum PoolInfoTrust
{
    NotAdvertised,
    SelfReported,
    Signed,
    RegistryVerified,
    RegistryMismatch,
}

/// <summary>
/// A pool's self-described fee / payout terms — the <c>pool-info/v1</c> object
/// from docs/POOL-FEE-TRANSPARENCY.md. Delivered either via an inbound
/// <c>pool.info</c> stratum notification or an HTTPS <c>.well-known</c> file.
///
/// Parsing is manual (JsonElement) on purpose: the miner is NativeAOT and has
/// no reflection-based deserializer, and this object arrives from an untrusted
/// peer so we validate field-by-field and never throw into the read loop.
/// </summary>
public sealed record PoolInfo(
    string  PoolName,
    double  FeePercent,
    string  PayoutScheme,
    string? MinPayout,
    long?   PayoutIntervalSec,
    string? Updated)
{
    public PoolInfoTrust Trust { get; init; } = PoolInfoTrust.SelfReported;

    public const string Schema = "pool-info/v1";

    /// <summary>Parse from a raw JSON object (the <c>pool.info</c> params[0] or
    /// the <c>.well-known</c> body). Returns false — and a null result — on ANY
    /// problem (wrong/absent schema, missing MUST field, out-of-range fee). The
    /// caller treats false as "not advertised" and keeps mining.</summary>
    public static bool TryParse(ReadOnlySpan<byte> utf8Json, out PoolInfo? info)
    {
        info = null;
        try
        {
            using var doc = JsonDocument.Parse(utf8Json.ToArray());
            return TryParse(doc.RootElement, out info);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Parse from an already-parsed JSON object.</summary>
    public static bool TryParse(JsonElement obj, out PoolInfo? info)
    {
        info = null;
        if (obj.ValueKind != JsonValueKind.Object)
            return false;

        // schema (MUST be exactly pool-info/v1)
        if (!obj.TryGetProperty("schema", out var schemaEl)
            || schemaEl.ValueKind != JsonValueKind.String
            || schemaEl.GetString() != Schema)
            return false;

        // fee_percent (MUST, 0..100)
        if (!obj.TryGetProperty("fee_percent", out var feeEl)
            || feeEl.ValueKind != JsonValueKind.Number
            || !feeEl.TryGetDouble(out var fee)
            || !double.IsFinite(fee) || fee < 0.0 || fee > 100.0)
            return false;

        // payout_scheme (MUST)
        if (!obj.TryGetProperty("payout_scheme", out var schemeEl)
            || schemeEl.ValueKind != JsonValueKind.String)
            return false;
        string scheme = schemeEl.GetString() ?? "";
        if (scheme.Length == 0)
            return false;

        string poolName = obj.TryGetProperty("pool_name", out var nameEl)
                          && nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString() ?? "" : "";

        string? minPayout = obj.TryGetProperty("min_payout", out var mpEl)
                            && mpEl.ValueKind == JsonValueKind.String
            ? mpEl.GetString() : null;

        long? interval = obj.TryGetProperty("payout_interval_sec", out var ivEl)
                         && ivEl.ValueKind == JsonValueKind.Number
                         && ivEl.TryGetInt64(out var iv)
            ? iv : null;

        string? updated = obj.TryGetProperty("updated", out var upEl)
                          && upEl.ValueKind == JsonValueKind.String
            ? upEl.GetString() : null;

        // NOTE: signature / key_id are accepted on the wire but NOT verified
        // yet, so trust stays SelfReported — we never claim "signed" for an
        // unverified signature.
        info = new PoolInfo(poolName, fee, scheme, minPayout, interval, updated)
        {
            Trust = PoolInfoTrust.SelfReported,
        };
        return true;
    }

    /// <summary>Short human label for the trust level, for banners/logs.</summary>
    public static string TrustLabel(PoolInfoTrust t) => t switch
    {
        PoolInfoTrust.SelfReported     => "advertised",
        PoolInfoTrust.Signed           => "signed",
        PoolInfoTrust.RegistryVerified => "verified: registry",
        PoolInfoTrust.RegistryMismatch => "⚠ registry mismatch",
        _                              => "not advertised",
    };
}
