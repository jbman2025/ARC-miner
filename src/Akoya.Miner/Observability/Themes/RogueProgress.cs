using System.Globalization;

namespace Akoya.Miner.Observability.Themes;

/// <summary>
/// Meta-progression for the rogue theme: the numbers that outlive a session.
///
/// Roguelikes separate the RUN (this session — shares slain, depth reached, dies
/// on restart) from META-PROGRESSION (lifetime totals that persist). Without the
/// second half every rig sits at LVL 1 forever, because a session's share count
/// starts at zero each launch, and the theme reads as a reskin rather than a game.
///
/// Also holds the short-lived "moment" flags (first blood, block find, personal
/// best) and the hashrate history the sparkline draws. Those are state, which
/// means <see cref="RogueTheme"/> is not a pure function of its context any more
/// — but the state lives HERE, behind an explicit <see cref="Observe"/> call, so
/// a theme still cannot invent anything: it renders what it was told.
///
/// Persistence is a handful of key=value lines, parsed by hand. No reflection,
/// no JSON serializer — this ships as Native AOT, and a progress file is never
/// worth risking a trim-related surprise for.
/// </summary>
internal sealed class RogueProgress
{
    // How long a "moment" stays lit. Long enough to notice on a 1 Hz redraw,
    // short enough that the panel doesn't permanently wear a party hat.
    private static readonly long MomentTicks = TimeSpan.TicksPerSecond * 20;
    private static readonly long SaveEveryTicks = TimeSpan.TicksPerSecond * 60;

    /// <summary>Samples kept for the sparkline. At the default 1 s redraw this
    /// is about a minute of history, which is enough to show a dip without the
    /// line becoming a flat average of everything.</summary>
    public const int HistoryLength = 60;

    public long LifetimeShares { get; private set; }
    public long LifetimeBlocks { get; private set; }
    public double BestHashrate { get; private set; }

    private long _sessionShareBase = -1;
    private long _sessionBlockBase = -1;
    private long _firstBloodAt;
    private long _blockFindAt;
    private long _personalBestAt;
    private long _lastSavedAt;
    private bool _dirty;

    private readonly double[] _history = new double[HistoryLength];
    private int _historyCount;
    private int _historyHead;

    private readonly string _path;
    private readonly object _gate = new();

    public RogueProgress(string? path = null)
    {
        _path = path ?? DefaultPath;
        Load();
    }

    private static string DefaultPath
    {
        get
        {
            var overridePath = Akoya.Crypto.MinerEnv.Get("ARC_PROGRESS_FILE");
            if (!string.IsNullOrEmpty(overridePath)) return overridePath;
            var home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home))
                home = OperatingSystem.IsWindows()
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : "/root";
            return Path.Combine(home, ".arc-miner", "progress");
        }
    }

    private static RogueProgress? _shared;
    public static RogueProgress Shared => _shared ??= new RogueProgress();

    /// <summary>Fold one snapshot into the progression. Called once per redraw.
    /// Counters are converted to DELTAS against a per-session baseline, because
    /// the snapshot's totals restart at zero every launch while the lifetime
    /// figures must not.</summary>
    public void Observe(in Metrics.DashSnapshot snap, long nowTicks)
    {
        lock (_gate)
        {
            long accepted = snap.Accepted;
            long blocks = snap.BlockFinds;

            if (_sessionShareBase < 0) { _sessionShareBase = accepted; _sessionBlockBase = blocks; }

            // A counter going BACKWARDS means the miner restarted under us
            // (or Metrics was re-initialised); re-baseline instead of
            // subtracting into negative lifetime totals.
            if (accepted < _sessionShareBase) _sessionShareBase = accepted;
            if (blocks < _sessionBlockBase) _sessionBlockBase = blocks;

            long newShares = accepted - _sessionShareBase;
            long newBlocks = blocks - _sessionBlockBase;

            if (newShares > 0)
            {
                if (LifetimeShares == 0 && _firstBloodAt == 0) _firstBloodAt = nowTicks;
                LifetimeShares += newShares;
                _sessionShareBase = accepted;
                _dirty = true;
            }
            if (newBlocks > 0)
            {
                LifetimeBlocks += newBlocks;
                _sessionBlockBase = blocks;
                _blockFindAt = nowTicks;
                _dirty = true;
            }

            double hs = snap.TotalHashesPerSec;
            if (double.IsFinite(hs) && hs > 0)
            {
                // Only celebrate a genuine improvement, and only once the
                // hashrate has settled — the first samples after start ramp up
                // through every value below the real rate and would otherwise
                // fire "personal best" continuously for the first minute.
                if (_historyCount >= 5 && hs > BestHashrate * 1.001)
                {
                    if (BestHashrate > 0) _personalBestAt = nowTicks;
                    BestHashrate = hs;
                    _dirty = true;
                }
                else if (hs > BestHashrate) BestHashrate = hs;

                _history[_historyHead] = hs;
                _historyHead = (_historyHead + 1) % HistoryLength;
                if (_historyCount < HistoryLength) _historyCount++;
            }

            if (_dirty && nowTicks - _lastSavedAt > SaveEveryTicks)
            {
                _lastSavedAt = nowTicks;
                SaveLocked();
            }
        }
    }

    public bool FirstBloodLit(long nowTicks)
        => _firstBloodAt > 0 && nowTicks - _firstBloodAt < MomentTicks;

    public bool BlockFindLit(long nowTicks)
        => _blockFindAt > 0 && nowTicks - _blockFindAt < MomentTicks;

    public bool PersonalBestLit(long nowTicks)
        => _personalBestAt > 0 && nowTicks - _personalBestAt < MomentTicks;

    /// <summary>History oldest-first, for the sparkline.</summary>
    public double[] History()
    {
        lock (_gate)
        {
            var outp = new double[_historyCount];
            int start = _historyCount == HistoryLength ? _historyHead : 0;
            for (int i = 0; i < _historyCount; i++)
                outp[i] = _history[(start + i) % HistoryLength];
            return outp;
        }
    }

    public void Save()
    {
        lock (_gate) { SaveLocked(); }
    }

    private void SaveLocked()
    {
        if (!_dirty) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var inv = CultureInfo.InvariantCulture;
            File.WriteAllText(_path,
                $"shares={LifetimeShares.ToString(inv)}\n" +
                $"blocks={LifetimeBlocks.ToString(inv)}\n" +
                $"best_hs={BestHashrate.ToString("R", inv)}\n");
            _dirty = false;
        }
        catch { /* progress is decoration; never fail a rig over it */ }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            foreach (var line in File.ReadAllLines(_path))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                var val = line[(eq + 1)..].Trim();
                var inv = CultureInfo.InvariantCulture;
                switch (key)
                {
                    case "shares" when long.TryParse(val, NumberStyles.Integer, inv, out var s) && s >= 0:
                        LifetimeShares = s; break;
                    case "blocks" when long.TryParse(val, NumberStyles.Integer, inv, out var b) && b >= 0:
                        LifetimeBlocks = b; break;
                    case "best_hs" when double.TryParse(val, NumberStyles.Float, inv, out var h)
                                        && double.IsFinite(h) && h >= 0:
                        BestHashrate = h; break;
                }
            }
        }
        catch { /* a corrupt progress file starts you over, it does not stop mining */ }
    }
}
