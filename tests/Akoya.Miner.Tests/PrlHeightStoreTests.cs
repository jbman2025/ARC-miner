using Akoya.Miner.Algos.Prl;
using Xunit;

namespace Akoya.Miner.Tests;

/// <summary>
/// Pearl height persistence. Inherited from the old RankForkTests when rank-256
/// support was removed: the rank decision these tests originally backed is gone,
/// but the persistence itself still feeds the dashboard's fork counter across a
/// cold start, so its contract is still worth pinning.
/// </summary>
public class PrlHeightStoreTests
{
    [Fact]
    public void RemembersAHeightForTheNextProcess()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "arc-height-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("ARC_PRL_HEIGHT_FILE", tmp);
        try
        {
            // Nothing remembered yet.
            Assert.Equal(0, PrlHeightStore.LoadPersisted());

            PrlHeightStore.Persist(96_300);

            // The NEXT process starts with no live height but remembers.
            Assert.Equal(96_300, PrlHeightStore.LoadPersisted());
            Assert.Equal(96_300, PrlHeightStore.BestKnown(0));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARC_PRL_HEIGHT_FILE", null);
            try { File.Delete(tmp); } catch { }
        }
    }

    [Fact]
    public void ALiveHeightAlwaysWinsOverTheRememberedOne()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "arc-height-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("ARC_PRL_HEIGHT_FILE", tmp);
        try
        {
            PrlHeightStore.Persist(96_300);
            Assert.Equal(120_000, PrlHeightStore.BestKnown(120_000));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARC_PRL_HEIGHT_FILE", null);
            try { File.Delete(tmp); } catch { }
        }
    }

    [Fact]
    public void PersistenceNeverThrowsOnAnUnwritablePath()
    {
        // A hint file must never be able to take a rig down: Persist has to
        // swallow the failure and Load has to report "nothing remembered".
        //
        // The unwritable path is a regular FILE standing where the store needs a
        // DIRECTORY — Directory.CreateDirectory throws on that on both Windows
        // and Linux. The version this test inherited from RankForkTests used a
        // " bad" path segment instead and assumed the OS would reject it;
        // Windows accepts a leading space, so that path was actually writable
        // and the test was asserting against a premise that never held.
        var blocker = Path.Combine(Path.GetTempPath(), "arc-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "not a directory");
        Environment.SetEnvironmentVariable("ARC_PRL_HEIGHT_FILE", Path.Combine(blocker, "last-height"));
        try
        {
            PrlHeightStore.Persist(96_300);
            Assert.Equal(0, PrlHeightStore.LoadPersisted());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARC_PRL_HEIGHT_FILE", null);
            try { File.Delete(blocker); } catch { }
        }
    }
}
