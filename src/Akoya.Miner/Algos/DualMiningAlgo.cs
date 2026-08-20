using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Akoya.Miner.Config;

namespace Akoya.Miner.Algos;

internal sealed class DualMiningAlgo : IMiningAlgo
{
    private readonly IMiningAlgo _algo1;
    private readonly IMiningAlgo _algo2;

    public DualMiningAlgo(IMiningAlgo algo1, IMiningAlgo algo2)
    {
        _algo1 = algo1;
        _algo2 = algo2;
    }

    public string Name => $"{_algo1.Name}+{_algo2.Name}";

    public async Task<int> RunAsync(MinerOptions opts, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("dual");
        log.LogInformation("dual: starting concurrent mining for {Name}", Name);

        // Signal a CPU algo that it is sharing the box with a GPU algo so it
        // reserves a couple of logical CPUs for the GPU host loop (feeding the GPU
        // + candidate verification). Without this the CPU algo saturates all cores
        // and the GPU can't be fed promptly, costing GPU hashrate. Only set when
        // that algo is actually in the pair; never overrides an explicit user value.
        if (_algo1.Name == "rx" || _algo2.Name == "rx")
        {
            Environment.SetEnvironmentVariable("ARC_RX_DUAL", "1");
        }
        if (_algo1.Name == "gr" || _algo2.Name == "gr")
        {
            Environment.SetEnvironmentVariable("ARC_GR_DUAL", "1");
        }
        if (_algo1.Name == "nm" || _algo2.Name == "nm")
        {
            Environment.SetEnvironmentVariable("ARC_NM_DUAL", "1");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var t1 = Task.Run(async () =>
        {
            try
            {
                return await _algo1.RunAsync(opts, loggerFactory, linkedCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "dual: algorithm {Name} failed", _algo1.Name);
                return 1;
            }
        });

        var t2 = Task.Run(async () =>
        {
            try
            {
                return await _algo2.RunAsync(opts, loggerFactory, linkedCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "dual: algorithm {Name} failed", _algo2.Name);
                return 1;
            }
        });

        var completedTask = await Task.WhenAny(t1, t2).ConfigureAwait(false);
        int exitCode = await completedTask.ConfigureAwait(false);

        // Cancel the other algorithm task
        linkedCts.Cancel();

        // Wait for both tasks to yield / cancel gracefully
        try
        {
            await Task.WhenAll(t1, t2).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Suppress cancellations during teardown
        }

        log.LogInformation("dual: concurrent mining stopped (exit={Exit})", exitCode);
        return exitCode;
    }
}
