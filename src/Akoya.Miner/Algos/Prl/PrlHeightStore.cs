namespace Akoya.Miner.Algos.Prl;

/// <summary>
/// Remembers the last Pearl chain height this rig saw, across process restarts.
///
/// Lifted out of the old RankFork when rank-256 support was removed: the rank
/// decision this persistence was originally built for is gone, but the height
/// itself is still needed by <see cref="Akoya.Miner.Observability.Metrics.PrlForksCrossed"/>,
/// which counts forks the rig has mined past and would otherwise read 0 for the
/// first seconds of every cold start (on stratum, ConnectAsync returns no initial
/// job — it registers and waits for a notify, so there is a real window with no
/// live height).
///
/// Deliberately a single integer in a file rather than anything cleverer: it is a
/// display hint, so every path here swallows its errors. A hint file must never
/// be able to take a rig down.
/// </summary>
internal static class PrlHeightStore
{
    private static string FilePath
    {
        get
        {
            var overridePath = Akoya.Crypto.MinerEnv.Get("ARC_PRL_HEIGHT_FILE");
            if (!string.IsNullOrEmpty(overridePath)) return overridePath;
            var home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home))
                home = OperatingSystem.IsWindows()
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : "/root";
            return Path.Combine(home, ".arc-miner", "last-height");
        }
    }

    private static long _lastPersisted;

    /// <summary>Record the height for the next process. Throttled to once per
    /// 64 blocks: this runs on the job path and the value only has to be good
    /// enough to place the chain against the fork table.</summary>
    public static void Persist(long height)
    {
        if (height <= 0) return;
        var path = FilePath;
        long prev = Interlocked.Read(ref _lastPersisted);
        // Throttled — but never skip when the file is missing. Otherwise a run
        // that had already recorded a height would decline to re-create the file
        // if it were deleted (or if the path changed), leaving the next process
        // with nothing to read.
        if (height <= prev + 64 && File.Exists(path)) return;
        Interlocked.Exchange(ref _lastPersisted, height);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, height.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch { /* a rig must never fail because a hint file was unwritable */ }
    }

    /// <summary>Height remembered from a previous run, or 0.</summary>
    public static long LoadPersisted()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return 0;
            return long.TryParse(File.ReadAllText(path).Trim(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var h) && h > 0 ? h : 0;
        }
        catch { return 0; }
    }

    /// <summary>Best height estimate available: what this process has seen, else
    /// what the previous one recorded.</summary>
    public static long BestKnown(long liveHeight)
        => liveHeight > 0 ? liveHeight : LoadPersisted();
}
