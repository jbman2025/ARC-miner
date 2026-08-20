// GhostRider SOLO mining over getblocktemplate / submitblock against a
// raptoreumd (or other GhostRider-chain) node.
//
// SCOPE / CAVEAT: this builds a STANDARD Bitcoin-style coinbase (BIP34 height +
// a single P2PKH payout to the miner address) and a plain block. That is correct
// for regtest / simple GhostRider chains, and is a faithful solo scaffold. It
// does NOT implement Raptoreum mainnet's special coinbase consensus rules
// (smartnode / founder payments and the cbTx/DIP payload), so Raptoreum MAINNET
// solo blocks will be rejected — pool mining (GrStratumClient) is the supported
// production path there. The node connection, GBT parsing, GhostRider search and
// submitblock plumbing are all exercised regardless.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Akoya.Miner.Observability;
using Microsoft.Extensions.Logging;

namespace Akoya.Miner.Algos.Gr;

internal static class GrSolo
{
    // Dashboard slot: the CPU row Metrics.InitCpu appended after the GPUs. Index
    // 0 is a real GPU when dual-mining (gr+prl and friends), so never hardcode it.
    private static int CpuIndex => Metrics.CpuIndex >= 0 ? Metrics.CpuIndex : 0;

    private sealed record Work(
        int Version, byte[] PrevInternal, uint Time, uint Bits, long Height,
        byte[] CoinbaseTx, byte[] MerkleRootInternal, List<byte[]> RawTxs, long CoinbaseValue,
        uint[] Target);

    public static async Task MineAsync(string nodeUrl, string address, string password, int threads,
        double pollSec, string worker, ILogger log, CancellationToken ct)
    {
        // Node RPC auth: ARC_GR_RPC_USER/PASS, else the shared password as pass,
        // else a cookie file via ARC_GR_RPC_COOKIE.
        string user = Environment.GetEnvironmentVariable("ARC_GR_RPC_USER") ?? "";
        string pass = Environment.GetEnvironmentVariable("ARC_GR_RPC_PASS") ?? password;
        var cookie = Environment.GetEnvironmentVariable("ARC_GR_RPC_COOKIE");
        if (!string.IsNullOrEmpty(cookie) && File.Exists(cookie)) (user, pass) = GrRpcClient.ReadCookie(cookie);

        var payoutScript = AddressToP2pkhScript(address);

        using var rpc = new GrRpcClient(nodeUrl, user, pass, TimeSpan.FromSeconds(30));
        log.LogWarning("gr: SOLO mode — standard coinbase only; Raptoreum MAINNET requires smartnode/founder coinbase rules not implemented here (use pool mining for mainnet).");

        var counts = new long[threads * 8];
        long curHeight = -1;
        var box = new WorkBox();

        var workers = new Thread[threads];
        using var epochCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        for (int i = 0; i < threads; i++)
        {
            int idx = i;
            workers[i] = new Thread(() => Worker(idx, threads, box, counts, rpc, address, worker, log, epochCts.Token))
            { IsBackground = true, Name = $"gr-solo-{idx}", Priority = ThreadPriority.Normal };
            workers[i].Start();
        }
        // CPU-side flag only. SetPoolConnected is the GPU pool's indicator, and
        // claiming it here would light up the GPU row while dual-mining.
        Metrics.SetCpuPoolConnected(true);

        try
        {
            var reportSw = Stopwatch.StartNew();
            long lastTotal = 0; double lastSec = 0;
            var poll = TimeSpan.FromSeconds(pollSec);
            while (!ct.IsCancellationRequested)
            {
                Work work;
                try { work = await FetchTemplateAsync(rpc, payoutScript, ct).ConfigureAwait(false); }
                // UnsupportedChainException must escape: it is a permanent
                // property of the chain, so retrying just hides it behind a
                // warning loop.
                catch (Exception e) when (!ct.IsCancellationRequested && e is not UnsupportedChainException)
                {
                    log.LogWarning("gr: template fetch failed ({Msg})", e.Message);
                    await Task.Delay(poll, ct).ConfigureAwait(false);
                    continue;
                }

                if (work.Height != curHeight)
                {
                    curHeight = work.Height;
                    box.Publish(work);
                    log.LogInformation("gr: solo job height={H} bits={B:x8} reward={R:F8}",
                        work.Height, work.Bits, work.CoinbaseValue / 1e8);
                }

                await Task.Delay(poll, ct).ConfigureAwait(false);

                long total = 0;
                for (int i = 0; i < threads; i++) total += Volatile.Read(ref counts[i * 8]);
                double now = reportSw.Elapsed.TotalSeconds, dt = now - lastSec;
                double hs = dt > 0 ? (total - lastTotal) / dt : 0;
                lastTotal = total; lastSec = now;
                Metrics.SetHashRate(CpuIndex, hs, hs > 0 ? 1000.0 * threads / hs : 0);
            }
        }
        finally
        {
            Metrics.SetCpuPoolConnected(false);
            epochCts.Cancel();
            foreach (var w in workers) w.Join(TimeSpan.FromSeconds(2));
        }
    }

    private static unsafe void Worker(int idx, int threads, WorkBox box, long[] counts, GrRpcClient rpc,
        string address, string worker, ILogger log, CancellationToken ct)
    {
        nint ctx = GrNative.CreateCtx();
        if (ctx == nint.Zero) { log.LogError("gr: solo worker {Idx} ctx create failed — {Err}", idx, GrNative.LastError()); return; }
        const int lanes = GrNative.Lanes;

        var header = new byte[80];
        var blob = new byte[80 * lanes];   // 8 lanes of `header`, differing only in nonce
        var outbuf = new byte[GrNative.HashBytes * lanes];
        long local = 0, lastGen = -1;
        Work? work = null;
        try
        {
            uint nonce = (uint)(idx * lanes);
            uint stride = (uint)(threads * lanes);
            while (!ct.IsCancellationRequested)
            {
                var (w, gen) = box.Snapshot();
                if (w is null) { Thread.Sleep(50); continue; }
                if (gen != lastGen)
                {
                    work = w; lastGen = gen; nonce = (uint)(idx * lanes);
                    BuildHeader(work, header);
                    for (int l = 0; l < lanes; l++) Array.Copy(header, 0, blob, l * 80, 80);
                }

                for (int l = 0; l < lanes; l++)
                    BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(l * 80 + 76, 4), nonce + (uint)l);

                fixed (byte* pB = blob)
                fixed (byte* pO = outbuf)
                    GrNative.HashOcta(ctx, pB, 80, pO);

                for (int l = 0; l < lanes; l++)
                {
                    if (!GrHash.MeetsTarget(outbuf.AsSpan(l * 32, 32), work!.Target)) continue;

                    uint winner = nonce + (uint)l;
                    log.LogInformation("gr: solo BLOCK candidate! height={H} nonce={N:x8}", work.Height, winner);
                    _ = SubmitBlockAsync(rpc, work, winner, log, ct);
                }

                nonce += stride;
                local += lanes;
                Volatile.Write(ref counts[idx * 8], local);
                if ((local & 0x3FF) == 0) Metrics.TouchHeartbeat(CpuIndex);
            }
        }
        finally { GrNative.DestroyCtx(ctx); }
    }

    private static void BuildHeader(Work w, byte[] header)
    {
        Array.Clear(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), w.Version);
        Array.Copy(w.PrevInternal, 0, header, 4, 32);
        Array.Copy(w.MerkleRootInternal, 0, header, 36, 32);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(68, 4), w.Time);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(72, 4), w.Bits);
        // nonce [76,80) filled per candidate.
    }

    /// <summary>Thrown when the node's template demands consensus rules this
    /// solo builder does not implement. Fatal: retrying cannot help.</summary>
    internal sealed class UnsupportedChainException(string message) : Exception(message);

    /// <summary>
    /// Does this template require coinbase rules we do not build? Returns a
    /// human-readable reason, or null when a standard coinbase will be accepted.
    ///
    /// Keyed on the template's own fields rather than the chain name on purpose:
    /// what breaks us is smartnode/founder payouts and the cbTx (DIP) payload,
    /// not "mainnet" as such. A Raptoreum testnet with smartnodes enforced would
    /// reject our blocks too, and a bare GhostRider regtest chain without them
    /// is perfectly fine — so ask the template what it wants.
    ///
    /// Every block we mine against an enforcing chain is wasted work: the node
    /// rejects it at submit time, which previously showed up only as a
    /// "solo block rejected" line after the fact.
    /// </summary>
    internal static string? DescribeUnsupportedRules(JsonElement result)
    {
        // cbTx / DIP payload (Raptoreum, Dash): a special coinbase transaction
        // payload we do not construct.
        if (result.TryGetProperty("coinbase_payload", out var payload) &&
            payload.ValueKind == JsonValueKind.String &&
            !string.IsNullOrEmpty(payload.GetString()))
        {
            return "the node requires a cbTx coinbase payload (coinbase_payload)";
        }

        // Smartnode (Raptoreum) / masternode (Dash) payouts, under either spelling.
        foreach (var name in new[] { "smartnode", "masternode" })
        {
            if (result.TryGetProperty(name, out var payee) && HasPayees(payee))
            {
                return $"the node requires {name} payouts in the coinbase";
            }
            if (result.TryGetProperty($"{name}_payments_enforced", out var enforced) &&
                enforced.ValueKind == JsonValueKind.True)
            {
                return $"the node enforces {name} payments";
            }
        }

        // Superblock / treasury payouts.
        if (result.TryGetProperty("superblock", out var sb) && HasPayees(sb))
        {
            return "the node requires superblock (treasury) payouts in the coinbase";
        }

        return null;

        static bool HasPayees(JsonElement el) => el.ValueKind switch
        {
            JsonValueKind.Array => el.GetArrayLength() > 0,
            JsonValueKind.Object => el.EnumerateObject().Any(),
            _ => false,
        };
    }

    private static async Task<Work> FetchTemplateAsync(GrRpcClient rpc, byte[] payoutScript, CancellationToken ct)
    {
        // Raptoreum (Dash-lineage) is non-segwit; declare no optional rules.
        using var doc = await rpc.CallAsync("getblocktemplate", "[{\"rules\":[]}]", ct).ConfigureAwait(false);
        var r = doc.RootElement.GetProperty("result");

        if (Environment.GetEnvironmentVariable("ARC_GR_SOLO_FORCE") != "1" &&
            DescribeUnsupportedRules(r) is { } reason)
        {
            throw new UnsupportedChainException(
                $"gr: refusing to solo-mine — {reason}, which this builder does not implement, so every block " +
                "would be rejected at submit. Use pool mining for this chain, or set ARC_GR_SOLO_FORCE=1 to " +
                "override (blocks will almost certainly be rejected).");
        }

        int version = r.GetProperty("version").GetInt32();
        var prevHex = r.GetProperty("previousblockhash").GetString()!;
        var prevInternal = Convert.FromHexString(prevHex);
        Array.Reverse(prevInternal);   // display (BE) -> header internal (LE)
        uint curtime = (uint)r.GetProperty("curtime").GetInt64();
        uint bits = Convert.ToUInt32(r.GetProperty("bits").GetString(), 16);
        long height = r.GetProperty("height").GetInt64();
        long coinbaseValue = r.GetProperty("coinbasevalue").GetInt64();

        var (coinbase, coinbaseTxid) = BuildCoinbase(height, coinbaseValue, payoutScript);

        var rawTxs = new List<byte[]>();
        var txids = new List<byte[]> { coinbaseTxid };
        if (r.TryGetProperty("transactions", out var txs) && txs.ValueKind == JsonValueKind.Array)
        {
            foreach (var tx in txs.EnumerateArray())
            {
                rawTxs.Add(Convert.FromHexString(tx.GetProperty("data").GetString()!));
                var txidHex = (tx.TryGetProperty("txid", out var idEl) ? idEl.GetString() : tx.GetProperty("hash").GetString())!;
                var idInternal = Convert.FromHexString(txidHex);
                Array.Reverse(idInternal);
                txids.Add(idInternal);
            }
        }

        var merkle = MerkleRoot(txids);
        var target = TargetFromBits(bits);
        return new Work(version, prevInternal, curtime, bits, height, coinbase, merkle, rawTxs, coinbaseValue, target);
    }

    private static async Task SubmitBlockAsync(GrRpcClient rpc, Work w, uint nonce, ILogger log, CancellationToken ct)
    {
        var header = new byte[80];
        BuildHeader(w, header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76, 4), nonce);

        var block = new List<byte>(header.Length + 4 + w.CoinbaseTx.Length + 64);
        block.AddRange(header);
        WriteVarInt(block, (ulong)(1 + w.RawTxs.Count));
        block.AddRange(w.CoinbaseTx);
        foreach (var tx in w.RawTxs) block.AddRange(tx);

        var hex = Convert.ToHexStringLower(block.ToArray());
        try
        {
            using var doc = await rpc.CallAsync("submitblock", $"[\"{hex}\"]", ct).ConfigureAwait(false);
            var res = doc.RootElement.TryGetProperty("result", out var rr) && rr.ValueKind == JsonValueKind.String ? rr.GetString() : null;
            if (string.IsNullOrEmpty(res))
            {
                Metrics.IncBlockFind();
                Metrics.IncShareAccepted(CpuIndex);
                log.LogInformation("gr: solo BLOCK ACCEPTED height={H} nonce={N:x8}", w.Height, nonce);
            }
            else
            {
                Metrics.IncShareRejected(CpuIndex);
                log.LogWarning("gr: solo block rejected height={H}: {Res}", w.Height, res);
            }
        }
        catch (Exception e)
        {
            Metrics.IncShareRejected(CpuIndex);
            log.LogWarning("gr: solo submitblock failed height={H}: {Msg}", w.Height, e.Message);
        }
    }

    // ── block-assembly helpers ──────────────────────────────────────────────────

    private static byte[] Sha256d(ReadOnlySpan<byte> data)
    {
        Span<byte> a = stackalloc byte[32];
        SHA256.HashData(data, a);
        return SHA256.HashData(a);
    }

    private static void WriteVarInt(List<byte> b, ulong n)
    {
        if (n < 0xFD) { b.Add((byte)n); }
        else if (n <= 0xFFFF) { b.Add(0xFD); b.Add((byte)n); b.Add((byte)(n >> 8)); }
        else if (n <= 0xFFFFFFFF) { b.Add(0xFE); for (int i = 0; i < 4; i++) b.Add((byte)(n >> (8 * i))); }
        else { b.Add(0xFF); for (int i = 0; i < 8; i++) b.Add((byte)(n >> (8 * i))); }
    }

    private static byte[] ScriptNum(long n)
    {
        if (n == 0) return Array.Empty<byte>();
        var o = new List<byte>();
        long v = n;
        while (v != 0) { o.Add((byte)(v & 0xFF)); v >>= 8; }
        if ((o[^1] & 0x80) != 0) o.Add(0);
        return o.ToArray();
    }

    // Standard coinbase: BIP34 height in scriptSig, single P2PKH payout, no
    // witness (GhostRider chains are non-segwit). Returns (serialized, txid internal).
    private static (byte[] Tx, byte[] TxidInternal) BuildCoinbase(long height, long value, byte[] payoutScript)
    {
        var heightPush = ScriptNum(height);
        var scriptSig = new List<byte> { (byte)heightPush.Length };
        scriptSig.AddRange(heightPush);
        scriptSig.Add(0x00);   // extranonce placeholder

        var tx = new List<byte>();
        void Le(ulong val, int bytes) { for (int i = 0; i < bytes; i++) tx.Add((byte)(val >> (8 * i))); }
        Le(2, 4);                       // version
        tx.Add(0x01);                   // vin count
        tx.AddRange(new byte[32]);      // prevout hash
        Le(0xFFFFFFFF, 4);              // prevout index
        WriteVarInt(tx, (ulong)scriptSig.Count);
        tx.AddRange(scriptSig);
        Le(0xFFFFFFFF, 4);              // sequence
        tx.Add(0x01);                   // vout count
        Le((ulong)value, 8);            // value
        WriteVarInt(tx, (ulong)payoutScript.Length);
        tx.AddRange(payoutScript);
        Le(0, 4);                       // locktime

        var full = tx.ToArray();
        return (full, Sha256d(full));
    }

    private static byte[] MerkleRoot(List<byte[]> txidsInternal)
    {
        if (txidsInternal.Count == 0) return new byte[32];
        var layer = txidsInternal.Select(t => (byte[])t.Clone()).ToList();
        while (layer.Count > 1)
        {
            if (layer.Count % 2 == 1) layer.Add(layer[^1]);
            var next = new List<byte[]>(layer.Count / 2);
            for (int i = 0; i < layer.Count; i += 2)
                next.Add(Sha256d(layer[i].Concat(layer[i + 1]).ToArray()));
            layer = next;
        }
        return layer[0];
    }

    // Compact nBits -> 8-word little-endian target (word 7 most significant),
    // matching GrHash.MeetsTarget's layout.
    private static uint[] TargetFromBits(uint bits)
    {
        int exp = (int)(bits >> 24);
        uint mant = bits & 0x007FFFFF;
        var be = new byte[32];
        for (int i = 0; i < 3; i++)
        {
            int pos = exp - 1 - i;      // byte position from the most-significant end
            if (pos >= 0 && pos < 32) be[31 - pos] = (byte)(mant >> (8 * (2 - i)));
        }
        // be[] is big-endian (be[0] = MSB). Convert to 8 LE words, word 7 = MSW.
        var t = new uint[8];
        for (int w = 0; w < 8; w++)
        {
            int baseIdx = (7 - w) * 4;   // word 7 comes from be[0..4]
            t[w] = ((uint)be[baseIdx] << 24) | ((uint)be[baseIdx + 1] << 16) |
                   ((uint)be[baseIdx + 2] << 8) | be[baseIdx + 3];
        }
        return t;
    }

    // Base58Check-decode a P2PKH address into OP_DUP OP_HASH160 <20> OP_EQUALVERIFY
    // OP_CHECKSIG. (P2PKH is the coinbase payout form for GhostRider chains.)
    private static byte[] AddressToP2pkhScript(string address)
    {
        var payload = Base58CheckDecode(address);
        // payload = [version byte(s)][20-byte hash]. RTM uses a 1-byte version.
        if (payload.Length < 21)
            throw new InvalidOperationException($"gr: unsupported address (decoded {payload.Length} bytes): {address}");
        var h160 = payload[^20..];
        var script = new byte[25];
        script[0] = 0x76;                 // OP_DUP
        script[1] = 0xA9;                 // OP_HASH160
        script[2] = 0x14;                 // push 20
        Array.Copy(h160, 0, script, 3, 20);
        script[23] = 0x88;                // OP_EQUALVERIFY
        script[24] = 0xAC;                // OP_CHECKSIG
        return script;
    }

    private const string B58 = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    private static byte[] Base58CheckDecode(string s)
    {
        // base58 -> big integer -> bytes
        var num = System.Numerics.BigInteger.Zero;
        foreach (var c in s)
        {
            int d = B58.IndexOf(c);
            if (d < 0) throw new InvalidOperationException($"gr: invalid base58 char '{c}' in address");
            num = num * 58 + d;
        }
        var raw = num.ToByteArray(isUnsigned: true, isBigEndian: true);
        // restore leading-zero bytes (each leading '1' == one 0x00 byte)
        int leading = 0;
        foreach (var c in s) { if (c == '1') leading++; else break; }
        var full = new byte[leading + raw.Length];
        Array.Copy(raw, 0, full, leading, raw.Length);
        if (full.Length < 5) throw new InvalidOperationException("gr: address too short");
        var body = full[..^4];
        var checksum = full[^4..];
        var check = Sha256d(body);
        for (int i = 0; i < 4; i++)
            if (check[i] != checksum[i]) throw new InvalidOperationException("gr: address checksum mismatch");
        return body;
    }

    private sealed class WorkBox
    {
        private Work? _work;
        private long _gen;
        public void Publish(Work w) { Volatile.Write(ref _work, w); Interlocked.Increment(ref _gen); }
        public (Work? Work, long Gen) Snapshot()
        {
            long g = Interlocked.Read(ref _gen);
            return (Volatile.Read(ref _work), g);
        }
    }
}
