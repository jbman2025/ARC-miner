namespace Akoya.Crypto;

/// <summary>
/// Configuration env-var reader with the user-facing <c>ARC_</c> prefix.
/// Every variable is documented to end users as <c>ARC_*</c>; the legacy
/// <c>AKOYA_*</c> name is still honoured silently so existing rigs, launch
/// scripts and HiveOS templates keep working. ARC_ wins when both are set.
/// </summary>
public static class MinerEnv
{
    public static string? Get(string legacyName)
    {
        const string LegacyPrefix = "AKOYA_";
        if (legacyName.StartsWith(LegacyPrefix, StringComparison.Ordinal))
        {
            var v = Environment.GetEnvironmentVariable("ARC_" + legacyName[LegacyPrefix.Length..]);
            if (!string.IsNullOrEmpty(v)) return v;
        }
        var legacy = Environment.GetEnvironmentVariable(legacyName);
        return string.IsNullOrEmpty(legacy) ? null : legacy;
    }
}
