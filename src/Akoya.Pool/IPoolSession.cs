using System.Threading.Tasks;
using PearlPool.Proto.V2;

namespace Akoya.Pool;

public interface IPoolSession : IAsyncDisposable
{
    ReadOnlyMemory<byte> MinerId { get; }
    Task<ResumeResponse?> ConnectAsync(SessionIdentity identity, CancellationToken ct);
    Task RunStreamAsync(
        MiningSessionCallbacks callbacks,
        TimeSpan streamWatchdog,
        TimeSpan pongTimeout,
        int outboundDepthTrip,
        CancellationToken ct);
    ValueTask SubmitShareAsync(ShareSubmission share, CancellationToken ct);
}
