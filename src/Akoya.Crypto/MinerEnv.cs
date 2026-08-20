namespace Akoya.Crypto;

/// <summary>
/// Configuration env-var reader. All variables use the <c>ARC_</c> prefix.
/// Returns null for unset or empty values so callers can use <c>??</c> defaults.
/// </summary>
public static class MinerEnv
{
    public static string? Get(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(v) ? null : v;
    }
}
