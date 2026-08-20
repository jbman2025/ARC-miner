// Persisted control-config written by the control API.
//
// The runtime config (MinerOptions) is immutable and built once from env vars,
// so the control API cannot mutate a live miner. Instead it writes the operator's
// chosen pool/wallet/worker/algo here, then the process restarts and re-reads
// this file at startup. Precedence: when this file exists, its fields override
// the matching CLI flags / env vars (it is the source of truth for the four
// fields the UI manages), so a UI edit survives a restart even if the miner was
// originally launched with --wallet/--pool/--worker/--algo. Delete the file to
// revert to launch-time flags.
//
// Only the four managed fields live here. It is NOT where secrets go — the API
// password is supplied at launch (ARC_API_PASSWORD / --api-password), never
// written to disk by us. The file is written owner-only (0600 on POSIX).

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Akoya.Miner.Config;

/// <summary>Operator-settable fields the control API can change. All nullable: a null
/// field means "not managed here — leave the CLI/env value in place".</summary>
internal sealed class ControlConfig
{
    public string? PoolHost { get; set; }
    public int?    PoolPort { get; set; }
    public bool?   UseTls   { get; set; }
    public string? Wallet   { get; set; }
    public string? Worker   { get; set; }
    public string? Algo     { get; set; }

    /// <summary>Resolved control-file path. Override with ARC_CONTROL_FILE.
    /// Mirrors the session-file convention (~/.arc-miner, legacy ~/.akoya).</summary>
    public static string FilePath
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable("ARC_CONTROL_FILE");
            if (!string.IsNullOrEmpty(overridePath)) return overridePath;
            var home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home))
                home = OperatingSystem.IsWindows()
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : "/root";
            var legacy = Path.Combine(home, ".akoya", "control.json");
            if (File.Exists(legacy)) return legacy;
            return Path.Combine(home, ".arc-miner", "control.json");
        }
    }

    /// <summary>Load the control file, or an empty instance if it is missing or
    /// unreadable (a corrupt file must never brick startup).</summary>
    public static ControlConfig Load()
    {
        var cfg = new ControlConfig();
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return cfg;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return cfg;
            if (root.TryGetProperty("pool_host", out var h) && h.ValueKind == JsonValueKind.String)
                cfg.PoolHost = h.GetString();
            if (root.TryGetProperty("pool_port", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var pv))
                cfg.PoolPort = pv;
            if (root.TryGetProperty("use_tls", out var t) && (t.ValueKind == JsonValueKind.True || t.ValueKind == JsonValueKind.False))
                cfg.UseTls = t.GetBoolean();
            if (root.TryGetProperty("wallet", out var w) && w.ValueKind == JsonValueKind.String)
                cfg.Wallet = w.GetString();
            if (root.TryGetProperty("worker", out var n) && n.ValueKind == JsonValueKind.String)
                cfg.Worker = n.GetString();
            if (root.TryGetProperty("algo", out var a) && a.ValueKind == JsonValueKind.String)
                cfg.Algo = a.GetString();
        }
        catch { /* missing/corrupt → empty config, launch-time flags win */ }
        return cfg;
    }

    /// <summary>Merge non-null fields of <paramref name="updates"/> into the
    /// on-disk file and persist atomically. Fields left null on the update keep
    /// whatever was previously saved.</summary>
    public static void Merge(ControlConfig updates)
    {
        var cur = Load();
        if (updates.PoolHost is not null) cur.PoolHost = updates.PoolHost;
        if (updates.PoolPort is not null) cur.PoolPort = updates.PoolPort;
        if (updates.UseTls   is not null) cur.UseTls   = updates.UseTls;
        if (updates.Wallet   is not null) cur.Wallet   = updates.Wallet;
        if (updates.Worker   is not null) cur.Worker   = updates.Worker;
        if (updates.Algo     is not null) cur.Algo     = updates.Algo;
        cur.Save();
    }

    private void Save()
    {
        static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var sb = new StringBuilder(256);
        sb.Append('{');
        var first = true;
        void Field(string k, string v)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(k).Append("\":").Append(v);
        }
        if (PoolHost is not null) Field("pool_host", "\"" + Esc(PoolHost) + "\"");
        if (PoolPort is not null) Field("pool_port", PoolPort.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (UseTls   is not null) Field("use_tls", UseTls.Value ? "true" : "false");
        if (Wallet   is not null) Field("wallet", "\"" + Esc(Wallet) + "\"");
        if (Worker   is not null) Field("worker", "\"" + Esc(Worker) + "\"");
        if (Algo     is not null) Field("algo", "\"" + Esc(Algo) + "\"");
        sb.Append('}');

        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString());
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { /* best-effort perms */ }
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Apply the saved control config to the process environment before
    /// options are bound, so the four managed fields override CLI/env values.
    /// No-op when the file is absent.</summary>
    public static void ApplyToEnvironment(ILogger log)
    {
        var cfg = Load();
        var applied = new List<string>(6);
        void Set(string key, string? val, string label)
        {
            if (string.IsNullOrEmpty(val)) return;
            Environment.SetEnvironmentVariable(key, val);
            applied.Add(label);
        }
        Set("ARC_POOL_HOST",   cfg.PoolHost, "pool");
        if (cfg.PoolPort is int pp) Set("ARC_POOL_PORT", pp.ToString(System.Globalization.CultureInfo.InvariantCulture), "port");
        if (cfg.UseTls is bool tls) Set("ARC_POOL_TLS", tls ? "true" : "false", "tls");
        Set("ARC_POOL_WALLET", cfg.Wallet, "wallet");
        Set("ARC_POOL_WORKER", cfg.Worker, "worker");
        Set("ARC_ALGO",        cfg.Algo,   "algo");

        if (applied.Count > 0)
            log.LogInformation(
                "control: applied saved settings from {Path} ({Fields}) — these override --pool/--wallet/--worker/--algo; delete the file to revert",
                FilePath, string.Join(", ", applied));
    }
}
