using Akoya.Miner.Config;
using Microsoft.Extensions.Logging;

namespace Akoya.Miner.Algos;

// A mining algorithm selected by `--algo <name>`. Each implementation owns its
// FULL mining lifecycle for one algorithm — orchestrator construction, the
// reconnect/backoff loop, and ReconnectHint honoring — so a new (or dying) algo
// is a self-contained module that never touches another algo's path.
//
// Step 1 (the PRL wrap) intentionally keeps this minimal: it does NOT abstract
// the GPU worker, stratum dialect, or share builder — those are still Pearl-typed
// and get factored out only once a second algo exists to validate the shape
// (rule-of-two). selftest / autotune likewise remain PRL-hardcoded in Program
// for now; generalizing them is future work.
internal interface IMiningAlgo
{
    string Name { get; }

    // Runs the algorithm until <paramref name="ct"/> is cancelled (clean stop)
    // or a fatal condition is hit. Returns the process exit code: 0 = clean
    // shutdown, non-zero (e.g. 78 EX_CONFIG) = fatal, do-not-retry.
    Task<int> RunAsync(MinerOptions opts, ILoggerFactory loggerFactory, CancellationToken ct);
}
