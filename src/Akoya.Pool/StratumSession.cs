using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ChallengeSolver = Akoya.Crypto.ChallengeSolver;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using PearlPool.Proto.V2;

#pragma warning disable CA5351 // Do Not Use Broken Cryptographic Algorithms
#pragma warning disable CA5359 // Modify SslStream validation callback

namespace Akoya.Pool;

public sealed class StratumSession : IPoolSession
{
    private readonly string _host;
    private readonly int _port;
    private readonly bool _useTls;
    private readonly bool _tlsInsecure;
    private readonly ILogger<StratumSession> _log;
    private readonly CancellationTokenSource _cts = new();

    private TcpClient? _tcpClient;
    private Stream? _stream;
    private StreamReader? _reader;

    private byte[] _minerId = [];
    private string _walletAddress = "";
    private string _workerName = "";
    private string _agent = "";

    private byte[] _extranonce1 = [];
    private readonly List<string> _jobHistory = new();
    private readonly Dictionary<string, string> _sigmaToJobId = new();
    private int _requestId = 1;

    // Lines received before the read loop starts (the pearl/v1 handshake can
    // deliver set_mining_params / set_difficulty / notify ahead of the
    // authorize ack). Drained first by ReadLoopAsync so nothing is lost.
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _earlyLines = new();

    // Outstanding pearl.challenge_response request ids — their acks must not
    // be mistaken for share results by the read loop.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _challengeRespIds = new();

    // Most recent mining.set_difficulty value — array-form (pearl/v1) notify
    // does not carry a target, so the job target is synthesized from this.
    private double _lastDifficulty;

    // True after a pearl/v1 challenge handshake: the pool then also expects
    // positional submit params ([worker, job_id, plain_proof_b64]) instead of
    // the Pearl-stratum object form.
    private volatile bool _pearlV1;

    // Monotonic TickCount64 of the last inbound line. Volatile-written from the
    // read loop, Volatile-read from the idle watchdog. Drives reconnect when a
    // pool goes silent (half-open route, dropped without RST).
    private long _lastInboundTicks;

    private MiningSessionCallbacks? _callbacks;

    // pool-info/v1 (fee transparency, see docs/POOL-FEE-TRANSPARENCY.md). Latest
    // advertised pool terms, if any. An inbound pool.info notification overrides
    // a .well-known fetch (in-band is authoritative/fresher).
    private PoolInfo? _poolInfo;

    // OPT-IN: advertise the pool-info/v1 capability inside mining.configure.
    // OFF by default — adding an unknown token to a live handshake message could
    // break a pool that strictly validates configure params, so the safe
    // transports (.well-known + inbound pool.info) work without touching it.
    private static readonly bool s_poolInfoNegotiate =
        Akoya.Crypto.MinerEnv.Get("AKOYA_POOL_INFO_NEGOTIATE") == "1";

    // ON by default (kill-switch =0): fetch the pool's .well-known fee file once
    // at stream start. Separate connection, bounded, never blocks mining.
    private static readonly bool s_poolInfoWellKnown =
        Akoya.Crypto.MinerEnv.Get("AKOYA_POOL_INFO_WELLKNOWN") != "0";

    // Shared, bounded HTTP client for the .well-known fetch only.
    private static readonly System.Net.Http.HttpClient s_http =
        new() { Timeout = TimeSpan.FromSeconds(2) };

    public ReadOnlyMemory<byte> MinerId => _minerId;

    public StratumSession(string host, int port, bool useTls, bool tlsInsecure, ILogger<StratumSession> log)
    {
        _host = host;
        _port = port;
        _useTls = useTls;
        _tlsInsecure = tlsInsecure;
        _log = log;
    }

    public async Task<ResumeResponse?> ConnectAsync(SessionIdentity identity, CancellationToken ct)
    {
        _walletAddress = identity.WalletAddress;
        _workerName = identity.WorkerName;
        _agent = "ARC-miner/" + identity.MinerVersion;

        // Seed the difficulty prior from the requested d= (AKOYA_STRATUM_DIFF /
        // --diff) so that if a pool sends mining.notify BEFORE its first
        // set_difficulty, ParseArrayNotify synthesizes the target from the diff
        // we ASKED for instead of the trivially-easy challenge floor (32). Mining
        // a job at diff 32 against a pool whose real target is far higher yields
        // immediate "below_target" rejects (reason 20). The pool's own
        // set_difficulty still overwrites this the moment it arrives.
        var reqDiff = Akoya.Crypto.MinerEnv.Get("AKOYA_STRATUM_DIFF");
        if (long.TryParse(reqDiff, out var rd) && rd > 0)
            Volatile.Write(ref _lastDifficulty, rd);

        // Generate stable 16 B minerId from wallet & worker
        _minerId = MD5.HashData(Encoding.UTF8.GetBytes($"{_walletAddress}:{_workerName}"));

        _log.LogDebug("stratum: connecting to {Host}:{Port} (tls={UseTls})...", _host, _port, _useTls);
        _tcpClient = new TcpClient();
        _tcpClient.NoDelay = true;
        await _tcpClient.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
        _log.LogDebug("stratum: TCP socket connected.");

        // OS-level TCP keepalive. Independent of any application-layer
        // keepalive: it keeps NAT/firewall state alive and lets us detect a
        // dead peer that silently dropped the route. Probe after 30s idle,
        // then every 10s, drop after 4 missed probes (~70s to detection).
        try
        {
            var sock = _tcpClient.Client;
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
            sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
            sock.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 4);
        }
        catch (SocketException) { /* not all platforms expose every knob — best effort */ }
        catch (NotSupportedException) { /* ditto */ }

        Stream networkStream = _tcpClient.GetStream();
        string proto = "plaintext";
        if (_useTls)
        {
            _log.LogDebug("stratum: starting TLS handshake...");
            // Pool TLS is encryption-in-transit, not identity verification: mining
            // pools almost universally serve self-signed / name-mismatched certs
            // (xmrig and our own prl-proxy accept any), so requiring a trusted CA
            // chain just blocks every TLS pool. We accept the cert by default and
            // log its SHA-256 — see AcceptPoolCert for optional pinning.
            var sslStream = new SslStream(networkStream, leaveInnerStreamOpen: false, AcceptPoolCert);

            // Explicit options for cross-pool compatibility. Letting the OS pick
            // defaults caused handshake failures on pools that only negotiate
            // TLS 1.2 (or only 1.3): we now offer both. TargetHost is the SNI —
            // pools sharing an edge/LB by hostname reject a handshake without it.
            var sslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = _host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = AcceptPoolCert,
            };

            // Bound the handshake so a pool that accepts the TCP connect but
            // never completes TLS (common with plain-TCP-only ports mistakenly
            // dialed as TLS) fails fast into the reconnect loop instead of
            // hanging the whole miner.
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCts.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                await sslStream.AuthenticateAsClientAsync(sslOptions, handshakeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"stratum: TLS handshake to {_host}:{_port} timed out after 15s — " +
                    "the port may be plain-TCP only (try without --tls).");
            }
            _stream = sslStream;
            proto = sslStream.SslProtocol.ToString();
            _log.LogDebug("stratum: TLS handshake completed (proto={Proto}).", proto);
        }
        else
        {
            _stream = networkStream;
        }

        _reader = new StreamReader(_stream, Encoding.UTF8);
        _log.LogDebug("stratum: streams initialized.");

        // pearl/v1 pools (e.g. AlphaPool) speak FIRST: immediately after the
        // TCP connect they send a pearl.challenge that must be solved before
        // anything else is accepted. "Pool speaks first with a challenge" is
        // the detection — no configuration needed. Pools that wait for the
        // client (Akoya Pearl-stratum) simply time the window out (~1.5 s,
        // AKOYA_CHALLENGE_WAIT_MS) and take the legacy path below.
        var firstLine = await TryReadEarlyLineAsync(ct).ConfigureAwait(false);
        if (firstLine is not null)
        {
            var firstMsg = TryDeserialize(firstLine);
            if (firstMsg?.Method == "pearl.challenge" && firstMsg.Params is JsonElement chParams)
            {
                await ConnectPearlV1Async(chParams.GetRawText(), proto, ct).ConfigureAwait(false);
                return null;
            }
            // Unsolicited but not a challenge — hand it to the read loop.
            _earlyLines.Enqueue(firstLine);
        }

        // 1. Authorize directly (skip subscribe which is unsupported on Pearl stratum)
        int authId = _requestId++;
        var authReq = new StratumAuthorizeRequest
        {
            Id = authId,
            Method = "mining.authorize",
            Params = new StratumAuthorizeParams
            {
                Wallet = _walletAddress,
                Worker = _workerName,
                Agent = _agent
            }
        };
        var authReqJson = JsonSerializer.Serialize(authReq, StratumJsonContext.Default.StratumAuthorizeRequest);
        _log.LogDebug("stratum: sending authorize request: {Json}", authReqJson);
        await WriteLineAsync(authReqJson, ct).ConfigureAwait(false);

        // Read until the authorize RESPONSE (matched by id). Some pools
        // (observed: suprnova) interleave the first mining.notify /
        // set_difficulty BEFORE the auth ack — those lines must be buffered
        // for the read loop, not consumed here. Treating the first line as
        // the auth response swallowed the pool's only job notify (worker sat
        // jobless until the next block) and the real auth ack was then
        // misread by the read loop as a share result.
        StratumMessage? authMsg = null;
        while (authMsg is null)
        {
            var authLine = await ReadLineDirectAsync(ct).ConfigureAwait(false);
            if (authLine == null) throw new InvalidOperationException("Pool disconnected during authorization");
            _log.LogDebug("stratum: authorization-phase line: {Line}", authLine);

            var msg = TryDeserialize(authLine);
            if (msg == null)
            {
                throw new InvalidOperationException("stratum: authorization response was invalid or empty JSON");
            }
            if (msg.Method is null && msg.Id == authId)
            {
                authMsg = msg;
                break;
            }
            // notify / set_difficulty / other notification ahead of the auth
            // ack — preserve order for the read loop.
            _earlyLines.Enqueue(authLine);
        }
        if (authMsg.Error != null && authMsg.Error.Value.ValueKind != JsonValueKind.Null)
        {
            throw new InvalidOperationException($"stratum: authorization failed: {authMsg.Error.Value}");
        }
        if (authMsg.Result is { } authResult && authResult.ValueKind is JsonValueKind.False or JsonValueKind.Null)
        {
            throw new InvalidOperationException("stratum: authorization rejected by pool (check wallet address format)");
        }

        _log.LogInformation("✓ connected & authorized — pool={Host}:{Port} proto={Proto} worker={Worker}",
            _host, _port, proto, _workerName);
        return null;
    }

    // ── pearl/v1 connection challenge ────────────────────────────────────────
    //
    // Wire format (captured from a live AlphaPool handshake):
    //   pool →  {"method":"pearl.challenge","params":{"seed":"<64 hex>","difficulty":32}}
    //   miner → {"id":N,"method":"pearl.challenge_response","params":{"nonce":"<16 hex>","seed":"<hex>"}}
    // A nonce wins when BLAKE3(seed || nonce_le64) has ≥ difficulty leading
    // zero bits (see Akoya.Crypto.ChallengeSolver). After the response the
    // session proceeds with configure / subscribe / array-form authorize whose
    // password carries the requested difficulty ("x;d=250000").

    private async Task<string?> TryReadEarlyLineAsync(CancellationToken ct)
    {
        int waitMs = int.TryParse(Akoya.Crypto.MinerEnv.Get("AKOYA_CHALLENGE_WAIT_MS"), out var w) && w >= 0
            ? w : 1500;
        if (waitMs == 0) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(waitMs);
        try { return await ReadLineDirectAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    private static StratumMessage? TryDeserialize(string line)
    {
        try { return JsonSerializer.Deserialize(line, StratumJsonContext.Default.StratumMessage); }
        catch (JsonException) { return null; }
    }

    private static (byte[] Seed, string SeedHex, int Difficulty) ParseChallenge(string paramsRaw)
    {
        using var doc = JsonDocument.Parse(paramsRaw);
        var seedHex = doc.RootElement.GetProperty("seed").GetString()
            ?? throw new InvalidOperationException("pearl.challenge missing seed");
        int difficulty = doc.RootElement.GetProperty("difficulty").GetInt32();
        var seed = Convert.FromHexString(seedHex);
        if (seed.Length != 32)
            throw new InvalidOperationException($"pearl.challenge seed is {seed.Length} bytes (expected 32)");
        return (seed, seedHex, difficulty);
    }

    private static int MaxChallengeDifficulty =>
        int.TryParse(Akoya.Crypto.MinerEnv.Get("AKOYA_CHALLENGE_MAX_DIFF"), out var d) && d > 0
            ? d : 40;   // 2^40 ≈ minutes-to-hours on CPU; anything above is hostile/buggy

    /// <summary>Solve a challenge and send the response. Returns the request
    /// id used (tracked in <see cref="_challengeRespIds"/> so the read loop
    /// doesn't misread the ack as a share result).</summary>
    private async Task<int> SolveAndRespondAsync(string paramsRaw, CancellationToken ct)
    {
        var (seed, seedHex, difficulty) = ParseChallenge(paramsRaw);
        if (difficulty > MaxChallengeDifficulty)
            throw new InvalidOperationException(
                $"pearl.challenge difficulty={difficulty} exceeds ARC_CHALLENGE_MAX_DIFF={MaxChallengeDifficulty} — refusing to grind");

        _log.LogInformation(
            "stratum: solving pearl/v1 connection challenge (diff={Diff} ≈ 2^diff hashes, {Threads} CPU threads)…",
            difficulty, Environment.ProcessorCount);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ulong? nonce = await Task.Run(() => ChallengeSolver.Solve(seed, difficulty, ct), ct).ConfigureAwait(false);
        sw.Stop();
        if (nonce is null) throw new OperationCanceledException("challenge solve cancelled");
        if (!ChallengeSolver.Verify(seed, nonce.Value, difficulty))
            throw new InvalidOperationException("challenge solver returned a non-winning nonce (bug)");
        _log.LogInformation(
            "stratum: ✓ challenge solved nonce={Nonce:x16} in {Sec:F1}s", nonce.Value, sw.Elapsed.TotalSeconds);

        int respId = _requestId++;
        _challengeRespIds.TryAdd(respId, 0);
        await WriteLineAsync(
            $"{{\"id\":{respId},\"method\":\"pearl.challenge_response\",\"params\":{{\"nonce\":\"{nonce.Value:x16}\",\"seed\":\"{seedHex}\"}}}}",
            ct).ConfigureAwait(false);
        return respId;
    }

    /// <summary>Connect-time handshake for challenge-first (pearl/v1) pools:
    /// solve challenge → configure → subscribe → array-form authorize with the
    /// difficulty request in the password. Job/diff notifications that arrive
    /// before the authorize ack are buffered for the read loop.</summary>
    private async Task ConnectPearlV1Async(string challengeParamsRaw, string proto, CancellationToken ct)
    {
        _pearlV1 = true;
        int respId = await SolveAndRespondAsync(challengeParamsRaw, ct).ConfigureAwait(false);

        int cfgId = _requestId++;
        // Default capability list is unchanged ([\"pearl/v1\"]). Only when the
        // operator opts in (AKOYA_POOL_INFO_NEGOTIATE=1) do we add the
        // pool-info/v1 token — see s_poolInfoNegotiate.
        string cfgCaps = s_poolInfoNegotiate ? "[\"pearl/v1\",\"pool-info/v1\"]" : "[\"pearl/v1\"]";
        await WriteLineAsync($"{{\"id\":{cfgId},\"method\":\"mining.configure\",\"params\":[{cfgCaps},{{}}]}}", ct)
            .ConfigureAwait(false);
        int subId = _requestId++;
        await WriteLineAsync($"{{\"id\":{subId},\"method\":\"mining.subscribe\",\"params\":[\"{JsonEsc(_agent)}\"]}}", ct)
            .ConfigureAwait(false);
        int authId = _requestId++;
        string password = BuildStratumPassword();
        await WriteLineAsync(
            $"{{\"id\":{authId},\"method\":\"mining.authorize\",\"params\":[\"{JsonEsc(_walletAddress)}.{JsonEsc(_workerName)}\",\"{JsonEsc(password)}\"]}}",
            ct).ConfigureAwait(false);

        while (true)
        {
            var line = await ReadLineDirectAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Pool disconnected during pearl/v1 authorization (challenge response rejected or expired?)");
            var msg = TryDeserialize(line);
            if (msg is null) continue;

            if (msg.Method is null && msg.Id == respId)
            {
                _challengeRespIds.TryRemove(respId, out _);
                if (msg.Result is { ValueKind: JsonValueKind.False })
                    throw new InvalidOperationException("stratum: pool rejected the challenge response");
                continue;
            }
            if (msg.Method is null && (msg.Id == cfgId || msg.Id == subId)) continue;
            if (msg.Method is null && msg.Id == authId)
            {
                if (msg.Error is { ValueKind: not JsonValueKind.Null } err)
                    throw new InvalidOperationException($"stratum: authorization failed: {err}");
                if (msg.Result is { } r && r.ValueKind is JsonValueKind.False or JsonValueKind.Null)
                    throw new InvalidOperationException("stratum: pearl/v1 authorize rejected");
                break;
            }
            // notify / set_difficulty / set_mining_params ahead of the auth
            // ack — preserve order for the read loop.
            _earlyLines.Enqueue(line);
        }

        _log.LogInformation(
            "✓ connected & authorized (pearl/v1 challenge handshake) — pool={Host}:{Port} proto={Proto} worker={Worker} password={Pw}",
            _host, _port, proto, _workerName, BuildStratumPassword());
    }

    /// <summary>Positional pearl/v1 mining.notify → StratumNotifyParams.
    /// Layout (captured live): [job_id, prev_hash, header, height, …]; the
    /// target is synthesized from the last mining.set_difficulty (the pool
    /// sends set_difficulty before the first notify).</summary>
    private StratumNotifyParams? ParseArrayNotify(JsonElement arr)
    {
        if (arr.GetArrayLength() < 4)
        {
            _log.LogWarning("stratum: array-form notify with {N} params — ignoring", arr.GetArrayLength());
            return null;
        }
        double diff = Volatile.Read(ref _lastDifficulty);
        if (diff <= 0)
        {
            // Reached only when the pool sent neither a set_difficulty NOR was a
            // d= requested (--diff). diff=32 is the challenge floor and is almost
            // certainly far below the pool's real share target → shares will be
            // rejected "below_target". Tell the operator how to fix it.
            _log.LogWarning(
                "stratum: notify before any set_difficulty and no --diff requested — "
                + "assuming diff=32; shares will likely be rejected below_target. "
                + "Pass --diff <n> (e.g. AlphaPool --diff 250000) to set the share difficulty.");
            diff = 32;
        }
        return new StratumNotifyParams
        {
            JobId  = arr[0].GetString() ?? "",
            Header = arr[2].GetString() ?? "",
            Height = arr[3].ValueKind == JsonValueKind.Number ? arr[3].GetInt64() : 0,
            Target = NbitsToTargetHex(DifficultyToNbits(diff)),
        };
    }

    /// <summary>Expand compact nbits to the 32-byte big-endian target, hex
    /// encoded — the same format object-form notify carries on the wire.</summary>
    internal static string NbitsToTargetHex(uint nbits)
    {
        int exp = (int)(nbits >> 24);
        uint mant = nbits & 0xFFFFFF;
        var target = new byte[32];
        for (int i = 0; i < 3; i++)
        {
            int pos = 32 - exp + i;
            if (pos is >= 0 and < 32)
                target[pos] = (byte)(mant >> (8 * (2 - i)));
        }
        return Convert.ToHexString(target).ToLowerInvariant();
    }

    private static string BuildStratumPassword()
    {
        string pw = Akoya.Crypto.MinerEnv.Get("AKOYA_STRATUM_PASSWORD") ?? "x";
        var dEnv = Akoya.Crypto.MinerEnv.Get("AKOYA_STRATUM_DIFF");
        if (long.TryParse(dEnv, out var d) && d > 0 && !pw.Contains("d=", StringComparison.OrdinalIgnoreCase))
            pw += $";d={d}";
        return pw;
    }

    private static string JsonEsc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>Pool-TLS certificate policy. Pool TLS provides encryption-in-
    /// transit, not server identity — pools serve self-signed / name-mismatched
    /// certs — so we accept by default (like xmrig and prl-proxy) rather than
    /// requiring a CA chain. If AKOYA_POOL_TLS_FINGERPRINT is set, the cert's
    /// SHA-256 must match it (trust-on-first-use style pinning); otherwise we
    /// accept and log the fingerprint so an operator can pin it later.
    /// <c>--tls-insecure</c> forces accept-anything (ignores the pin).</summary>
    private bool AcceptPoolCert(object sender, X509Certificate? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (_tlsInsecure) return true;       // explicit accept-anything override
        if (cert is null) return true;        // anonymous cipher — nothing to pin

        string fp = Convert.ToHexString(SHA256.HashData(cert.GetRawCertData())).ToLowerInvariant();
        string? pin = Akoya.Crypto.MinerEnv.Get("AKOYA_POOL_TLS_FINGERPRINT")
            ?.Replace(":", "").Replace(" ", "").ToLowerInvariant();

        if (!string.IsNullOrEmpty(pin) && pin != fp)
        {
            _log.LogError(
                "stratum: pool TLS cert sha256={Fp} does not match pinned ARC_POOL_TLS_FINGERPRINT — refusing (possible MITM)",
                fp);
            return false;
        }

        if (errors != SslPolicyErrors.None)
            _log.LogDebug("stratum: accepting pool TLS cert (unverified: {Errors}) sha256={Fp}", errors, fp);
        return true;
    }

    public async Task RunStreamAsync(
        MiningSessionCallbacks callbacks,
        TimeSpan streamWatchdog,
        TimeSpan pongTimeout,
        int outboundDepthTrip,
        CancellationToken ct)
    {
        _callbacks = callbacks;
        Volatile.Write(ref _lastInboundTicks, Environment.TickCount64);

        // Linked CTS: the idle watchdog cancels it to force the read loop to
        // bail out, which returns from RunStreamAsync and lets Program.cs run
        // its reconnect loop. Without this a pool that silently stops sending
        // (no notify, no RST) would wedge the miner forever.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Safe out-of-band fee-transparency transport: fetch the pool's
        // .well-known file once, fire-and-forget + bounded. Never gates mining;
        // an inbound pool.info (if the pool sends one) overrides the result.
        if (s_poolInfoWellKnown)
            _ = TryFetchWellKnownPoolInfoAsync(linked.Token);

        var readTask     = ReadLoopAsync(linked.Token);
        var watchdogTask = streamWatchdog > TimeSpan.Zero
            ? IdleWatchdogLoop(streamWatchdog, linked)
            : Task.CompletedTask;
        var keepAliveTask = KeepAliveLoop(linked.Token);

        try
        {
            await readTask.ConfigureAwait(false);
        }
        finally
        {
            linked.Cancel();
            try { await watchdogTask.ConfigureAwait(false); }  catch { /* shutdown */ }
            try { await keepAliveTask.ConfigureAwait(false); } catch { /* shutdown */ }
        }
    }

    /// <summary>Reconnect-forcing idle watchdog. Stratum has no protocol-level
    /// pong, so the only liveness signal is "did any line arrive". If the pool
    /// goes quiet for longer than <paramref name="budget"/> we cancel the
    /// linked token; the read loop unblocks and RunStreamAsync returns, which
    /// drives Program.cs's reconnect.</summary>
    private async Task IdleWatchdogLoop(TimeSpan budget, CancellationTokenSource linked)
    {
        var tick = TimeSpan.FromMilliseconds(Math.Max(1000, Math.Min(5000, budget.TotalMilliseconds / 3)));
        try
        {
            while (!linked.IsCancellationRequested)
            {
                await Task.Delay(tick, linked.Token).ConfigureAwait(false);
                var idle = Environment.TickCount64 - Volatile.Read(ref _lastInboundTicks);
                if (idle > budget.TotalMilliseconds)
                {
                    _log.LogWarning(
                        "stratum: idle watchdog tripped (no inbound for {Idle:F0}ms > {Budget:F0}ms) — forcing reconnect",
                        idle, budget.TotalMilliseconds);
                    linked.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    /// <summary>Application-layer keepalive. Many pools drop a worker that has
    /// submitted no shares for a few minutes ("idle worker" timeout). When the
    /// difficulty is high enough that shares are sparse, a periodic no-op keeps
    /// the session marked live. Sent as a benign mining.authorize re-assert,
    /// which every Pearl-stratum pool accepts (it just re-confirms the worker).
    ///
    /// OPT-IN: disabled by default. Enable with the --keepalive CLI flag or by
    /// setting AKOYA_STRATUM_KEEPALIVE_SEC to a positive interval (seconds).</summary>
    private async Task KeepAliveLoop(CancellationToken ct)
    {
        var sec = 0;   // off unless explicitly enabled
        var raw = Akoya.Crypto.MinerEnv.Get("AKOYA_STRATUM_KEEPALIVE_SEC");
        if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out var parsed)) sec = parsed;
        if (sec <= 0) return;

        var interval = TimeSpan.FromSeconds(sec);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                int reqId = _requestId++;
                var req = new StratumAuthorizeRequest
                {
                    Id = reqId,
                    Method = "mining.authorize",
                    Params = new StratumAuthorizeParams
                    {
                        Wallet = _walletAddress,
                        Worker = _workerName,
                        // Re-assert with the SAME agent as the initial authorize
                        // so the pool dedupes this to the existing worker instead
                        // of registering a second "Akoya-Miner/keepalive" worker.
                        Agent = _agent
                    }
                };
                var json = JsonSerializer.Serialize(req, StratumJsonContext.Default.StratumAuthorizeRequest);
                try
                {
                    await WriteLineAsync(json, ct).ConfigureAwait(false);
                    _log.LogDebug("stratum: keepalive sent (id={Id})", reqId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogDebug(ex, "stratum: keepalive write failed (peer likely gone)");
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    public async ValueTask SubmitShareAsync(ShareSubmission share, CancellationToken ct)
    {
        if (_stream == null) return;

        var sigmaHex = Convert.ToHexString(share.Sigma.Span).ToLowerInvariant();
        string jobId;
        lock (_sigmaToJobId)
        {
            if (!_sigmaToJobId.TryGetValue(sigmaHex, out var foundJobId))
            {
                foundJobId = Convert.ToHexString(share.Sigma.Span.Slice(0, 16)).ToLowerInvariant();
            }
            jobId = foundJobId;
        }

        // Serialize ShareSubmission to Bincode byte array and base64 encode it as the plain_proof
        var serializedBytes = BincodeSerializer.Serialize(share);
        var plainProofBase64 = Convert.ToBase64String(serializedBytes);

        int reqId = _requestId++;
        string json;
        if (_pearlV1)
        {
            // pearl/v1 pools take positional params: [worker, job_id, proof_b64]
            // (worker is the authorize username, wallet.workername).
            json = $"{{\"id\":{reqId},\"method\":\"mining.submit\",\"params\":[\"{JsonEsc(_walletAddress)}.{JsonEsc(_workerName)}\",\"{JsonEsc(jobId)}\",\"{plainProofBase64}\"]}}";
        }
        else
        {
            var request = new StratumSubmitRequest
            {
                Id = reqId,
                Method = "mining.submit",
                Params = new StratumSubmitParams
                {
                    JobId = jobId,
                    PlainProof = plainProofBase64
                }
            };
            json = JsonSerializer.Serialize(request, StratumJsonContext.Default.StratumSubmitRequest);
        }
        _log.LogInformation("stratum: submitting share (job={JobIdPrefix})", jobId[..Math.Min(8, jobId.Length)]);
        await WriteLineAsync(json, ct).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _reader != null)
            {
                // Drain lines buffered during the pearl/v1 handshake first so
                // the σ job / vardiff that arrived before the authorize ack is
                // processed in arrival order.
                string? line = _earlyLines.TryDequeue(out var early)
                    ? early
                    : await ReadLineDirectAsync(ct).ConfigureAwait(false);
                if (line == null)
                {
                    _log.LogWarning("stratum: stream ended by remote pool");
                    break;
                }

                // Liveness: any inbound line counts. The watchdog reads this.
                Volatile.Write(ref _lastInboundTicks, Environment.TickCount64);

                _log.LogDebug("stratum: line read: {Line}", line);
                try
                {
                    var msg = JsonSerializer.Deserialize(line, StratumJsonContext.Default.StratumMessage);
                    if (msg != null)
                    {
                        if (msg.Method == "mining.notify" && msg.Params is JsonElement notifyParams)
                        {
                            // Two wire forms: object {job_id, header, target,
                            // height} (Akoya Pearl-stratum / shim-translated)
                            // or positional pearl/v1 [job_id, prev_hash,
                            // header, height, …] where the share target is NOT
                            // carried — it derives from the current stratum
                            // difficulty (the same construction the AlphaPool
                            // shim used).
                            var notifyObj = notifyParams.ValueKind == JsonValueKind.Array
                                ? ParseArrayNotify(notifyParams)
                                : JsonSerializer.Deserialize(notifyParams.GetRawText(), StratumJsonContext.Default.StratumNotifyParams);
                            if (notifyObj != null)
                            {
                                var parsedJob = ParsePearlNotification(notifyObj);
                                if (_callbacks?.OnJob != null)
                                {
                                    await _callbacks.OnJob(parsedJob).ConfigureAwait(false);
                                }
                            }
                        }
                        else if (msg.Method == "mining.set_difficulty" && msg.Params is JsonElement diffParams)
                        {
                            // Params is normally [difficulty] (a single float),
                            // but tolerate a bare scalar too. The previous code
                            // read .Current WITHOUT MoveNext (undefined element)
                            // and threw on every vardiff message — vardiff never
                            // actually applied.
                            if (TryReadDifficulty(diffParams, out var diff) && diff > 0)
                            {
                                Volatile.Write(ref _lastDifficulty, diff);
                                uint nbits = DifficultyToNbits(diff);
                                _log.LogInformation(
                                    "stratum: set_difficulty {Diff} → nbits=0x{Nbits:X8}", diff, nbits);
                                if (_callbacks?.OnVardiff != null)
                                {
                                    await _callbacks.OnVardiff(new DifficultyAdjust
                                    {
                                        NewTargetNbits = nbits,
                                        Reason = "stratum",
                                        MeasuredHashrate = 0
                                    }).ConfigureAwait(false);
                                }
                            }
                            else
                            {
                                _log.LogWarning("stratum: set_difficulty with unparseable params: {Raw}", diffParams.GetRawText());
                            }
                        }
                        else if (msg.Method == "pool.info" && msg.Params is JsonElement poolInfoParams)
                        {
                            // pool-info/v1 (fee transparency). Purely additive:
                            // unknown/malformed advertisements are ignored and
                            // never break the read loop or mining.
                            await HandlePoolInfoAsync(poolInfoParams).ConfigureAwait(false);
                        }
                        else if (msg.Method == "pearl.challenge" && msg.Params is JsonElement chParams)
                        {
                            // Mid-session re-challenge (pool resets the gate).
                            // Solve on a worker thread — the read loop must
                            // keep pumping jobs while the CPU grinds.
                            var raw = chParams.GetRawText();
                            _log.LogWarning("stratum: mid-session pearl.challenge received — re-solving");
                            _ = Task.Run(async () =>
                            {
                                try { await SolveAndRespondAsync(raw, ct).ConfigureAwait(false); }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    _log.LogError(ex, "stratum: mid-session challenge solve failed");
                                }
                            }, ct);
                        }
                        else if (msg.Id != null && msg.Method == null
                                 && _challengeRespIds.TryRemove(msg.Id.Value, out _))
                        {
                            // Ack for a mid-session challenge response — not a share result.
                            bool ok = msg.Result is not { ValueKind: JsonValueKind.False };
                            _log.LogInformation("stratum: challenge response {Status}", ok ? "accepted" : "REJECTED");
                        }
                        else if (msg.Id != null && msg.Method == null)
                        {
                            // Share submission confirmation
                            if (_callbacks?.OnShareResult != null)
                            {
                                bool accepted = true;
                                string errorMsg = "";
                                if (msg.Error != null && msg.Error.Value.ValueKind != JsonValueKind.Null)
                                {
                                    accepted = false;
                                    errorMsg = msg.Error.Value.ToString();
                                }
                                else if (msg.Result != null && (msg.Result.Value.ValueKind == JsonValueKind.False || msg.Result.Value.ValueKind == JsonValueKind.Null))
                                {
                                    accepted = false;
                                }

                                await _callbacks.OnShareResult(new ShareResult
                                {
                                    Accepted = accepted,
                                    ComputedHash = Google.Protobuf.ByteString.Empty,
                                    Outcome = accepted ? "Accepted" : "Rejected",
                                    Message = errorMsg,
                                    IsBlockFind = false
                                }).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (JsonException ex)
                {
                    _log.LogError(ex, "stratum: failed to parse JSON message: {Line}",
                        line.Length > 300 ? line[..300] + "…" : line);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "stratum: read loop exception");
        }
    }

    private async Task WriteLineAsync(string line, CancellationToken ct)
    {
        if (_stream == null) return;
        _log.LogDebug("stratum: sending line: {String}", line);
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await _stream.WriteAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static byte[] HexToBytes(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return [];
        if (hex.Length % 2 != 0) hex = "0" + hex;
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        }
        return bytes;
    }

    private JobAssignment ParsePearlNotification(StratumNotifyParams p)
    {
        byte[] sigma = HexToBytes(p.Header);

        string headerHex = p.Header.ToLowerInvariant();
        lock (_sigmaToJobId)
        {
            _sigmaToJobId[headerHex] = p.JobId;
            if (_sigmaToJobId.Count > 100)
            {
                foreach (var key in _sigmaToJobId.Keys)
                {
                    _sigmaToJobId.Remove(key);
                    break;
                }
            }
        }

        byte[] jobIdBytes = new byte[16];
        if (Guid.TryParse(p.JobId, out var parsedGuid))
        {
            jobIdBytes = parsedGuid.ToByteArray();
        }
        else
        {
            byte[] rawJobBytes = Encoding.UTF8.GetBytes(p.JobId);
            byte[] sha256 = SHA256.HashData(rawJobBytes);
            Buffer.BlockCopy(sha256, 0, jobIdBytes, 0, 16);
        }

        byte[] targetBytes = HexToBytes(p.Target);
        uint targetNbits = TargetToNbits(targetBytes);
        if (Akoya.Crypto.MinerEnv.Get("AKOYA_FAKE_TARGET") == "1")
        {
            targetNbits = 0x207fffff;
        }

        byte[] bSeed = new byte[32];
        uint auditK = 0;

        return new JobAssignment
        {
            JobId = Google.Protobuf.ByteString.CopyFrom(jobIdBytes),
            Sigma = Google.Protobuf.ByteString.CopyFrom(sigma),
            TargetNbits = targetNbits,
            NetworkTargetNbits = targetNbits,
            BlockHeight = p.Height,
            ProtocolVersion = 2,
            BSeed = Google.Protobuf.ByteString.CopyFrom(bSeed),
            AuditK = auditK
        };
    }

    private static uint TargetToNbits(byte[] targetBytes)
    {
        int firstNonZero = -1;
        for (int i = 0; i < targetBytes.Length; i++)
        {
            if (targetBytes[i] != 0)
            {
                firstNonZero = i;
                break;
            }
        }
        if (firstNonZero == -1)
        {
            return 0;
        }
        int len = targetBytes.Length - firstNonZero;
        uint mantissa = 0;
        if (len >= 3)
        {
            mantissa = ((uint)targetBytes[firstNonZero] << 16) |
                       ((uint)targetBytes[firstNonZero + 1] << 8) |
                       (uint)targetBytes[firstNonZero + 2];
        }
        else if (len == 2)
        {
            mantissa = ((uint)targetBytes[firstNonZero] << 16) |
                       ((uint)targetBytes[firstNonZero + 1] << 8);
        }
        else
        {
            mantissa = (uint)targetBytes[firstNonZero] << 16;
        }

        if ((mantissa & 0x00800000u) != 0)
        {
            mantissa >>= 8;
            len++;
        }
        return ((uint)len << 24) | mantissa;
    }

    /// <summary>Read the difficulty value from a mining.set_difficulty params
    /// payload. Accepts the standard <c>[difficulty]</c> array form and a bare
    /// numeric scalar; ignores anything else.</summary>
    private static bool TryReadDifficulty(JsonElement p, out double diff)
    {
        diff = 0;
        if (p.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in p.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Number) return el.TryGetDouble(out diff);
                if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(),
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out diff))
                    return true;
            }
            return false;
        }
        if (p.ValueKind == JsonValueKind.Number) return p.TryGetDouble(out diff);
        if (p.ValueKind == JsonValueKind.String) return double.TryParse(p.GetString(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out diff);
        return false;
    }

    // Stratum difficulty-1 target (the classic "pdiff" base):
    // 0x00000000FFFF0000000000000000000000000000000000000000000000000000.
    // The share target for a given vardiff difficulty D is diff1Target / D.
    private static readonly BigInteger Diff1Target =
        new BigInteger(0xFFFFu) << (208);

    /// <summary>Convert a stratum vardiff difficulty into the compact nbits the
    /// miner feeds to its PoW target. target = diff1Target / difficulty, then
    /// encoded as a 32-byte big-endian value and compacted. Higher difficulty
    /// ⇒ smaller target ⇒ larger nbits exponent shrinks — i.e. harder.</summary>
    // Handle an inbound pool.info notification. params is [ {PoolInfo} ] (array)
    // or, tolerantly, a bare object. In-band info is authoritative and overrides
    // any prior .well-known result. Must never throw into the read loop.
    private async Task HandlePoolInfoAsync(JsonElement poolInfoParams)
    {
        try
        {
            JsonElement obj;
            if (poolInfoParams.ValueKind == JsonValueKind.Array && poolInfoParams.GetArrayLength() >= 1)
                obj = poolInfoParams[0];
            else if (poolInfoParams.ValueKind == JsonValueKind.Object)
                obj = poolInfoParams;
            else
                return;

            if (!PoolInfo.TryParse(obj, out var info) || info is null)
                return; // unknown schema / malformed → ignore, keep mining

            Volatile.Write(ref _poolInfo, info);
            if (_callbacks?.OnPoolInfo is { } cb)
                await cb(info).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "stratum: pool.info handling failed — ignored");
        }
    }

    // Fetch https://<host>/.well-known/mining-pool-info.json once. Bounded,
    // best-effort, error-swallowing — a failure (no web host, 404, timeout,
    // non-conforming body) just means "not advertised". Does not apply if an
    // inbound pool.info already set the terms.
    private async Task TryFetchWellKnownPoolInfoAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _poolInfo) is not null) return;
        try
        {
            var url = $"https://{_host}/.well-known/mining-pool-info.json";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var bytes = await s_http.GetByteArrayAsync(url, cts.Token).ConfigureAwait(false);
            // Bound the size and parse defensively.
            if (bytes.Length is > 0 and <= 8192
                && PoolInfo.TryParse(bytes, out var info) && info is not null)
            {
                // Only apply if in-band hasn't won the race.
                if (Interlocked.CompareExchange(ref _poolInfo, info, null) is null
                    && _callbacks?.OnPoolInfo is { } cb)
                    await cb(info).ConfigureAwait(false);
            }
        }
        catch
        {
            // never block mining on the fee fetch
        }
    }

    internal static uint DifficultyToNbits(double difficulty)
    {
        if (!(difficulty > 0) || !double.IsFinite(difficulty))
            difficulty = 1.0;

        // Scale to retain fractional difficulty precision through the integer
        // divide (vardiff often hands out values like 0.5 or 1234.75).
        const long scale = 1_000_000L;
        var scaledDiff = new BigInteger(Math.Max(1.0, difficulty * scale));
        BigInteger target = (Diff1Target * scale) / scaledDiff;

        // Render to a fixed 32-byte big-endian buffer for TargetToNbits.
        var raw = target.ToByteArray(isUnsigned: true, isBigEndian: true);
        var be32 = new byte[32];
        if (raw.Length <= 32)
            Buffer.BlockCopy(raw, 0, be32, 32 - raw.Length, raw.Length);
        else
            // Overflow (difficulty < ~min) — clamp to max target.
            Array.Fill(be32, (byte)0xFF);

        return TargetToNbits(be32);
    }

    private async Task<string?> ReadLineDirectAsync(CancellationToken ct)
    {
        if (_reader == null) return null;
        var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
        _log.LogDebug("stratum: ReadLineDirectAsync returning line: {Line}", line);
        return line;
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _reader?.Dispose();
        _stream?.Dispose();
        _tcpClient?.Dispose();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal static class BincodeSerializer
{
    public static byte[] Serialize(ShareSubmission share)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // PlainProof {
        //   m: usize (u64)
        //   n: usize (u64)
        //   k: usize (u64)
        //   noise_rank: usize (u64)
        //   a: MatrixMerkleProof
        //   bt: MatrixMerkleProof
        // }

        writer.Write((ulong)share.M);
        writer.Write((ulong)share.N);
        writer.Write((ulong)share.K);
        writer.Write((ulong)share.NoiseRank);

        var aRowIndices = share.ARowIndices ?? Array.Empty<uint>();
        var bColIndices = share.BColIndices ?? Array.Empty<uint>();

        // Write MatrixMerkleProof A
        WriteMatrixMerkleProof(writer, share.AProof, share.HashA.ToByteArray(), aRowIndices);

        // Write MatrixMerkleProof B
        WriteMatrixMerkleProof(writer, share.BProof, share.HashB.ToByteArray(), bColIndices);

        return ms.ToArray();
    }

    private static void WriteMatrixMerkleProof(BinaryWriter writer, MerkleProof proof, byte[] root, uint[] rowIndices)
    {
        // MatrixMerkleProof {
        //   proof: MerkleProof
        //   row_indices: Vec<warning> (Vec<u64> in bincode)
        // }
        WriteMerkleProof(writer, proof, root);

        writer.Write((ulong)rowIndices.Length);
        foreach (var idx in rowIndices)
        {
            writer.Write((ulong)idx);
        }
    }

    private static void WriteMerkleProof(BinaryWriter writer, MerkleProof proof, byte[] root)
    {
        // MerkleProof {
        //   leaf_data: Vec<[u8; 1024]> (Vec of 1024-byte arrays)
        //   leaf_indices: Vec<usize>
        //   total_leaves: usize
        //   root: Digest ([u8; 32])
        //   siblings: Vec<Digest> (Vec of [u8; 32] arrays)
        // }

        // leaf_data
        writer.Write((ulong)proof.LeafData.Count);
        foreach (var leaf in proof.LeafData)
        {
            writer.Write((ulong)leaf.Length);
            writer.Write(leaf.Span);
        }

        // leaf_indices
        writer.Write((ulong)proof.LeafIndices.Count);
        foreach (var idx in proof.LeafIndices)
        {
            writer.Write((ulong)idx);
        }

        // total_leaves
        writer.Write((ulong)proof.TotalLeaves);

        // root
        writer.Write(root);

        // siblings
        writer.Write((ulong)proof.Siblings.Count);
        foreach (var sib in proof.Siblings)
        {
            writer.Write(sib.Span);
        }
    }
}
