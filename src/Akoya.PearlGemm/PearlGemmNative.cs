// Akoya.PearlGemm — P/Invoke surface for the pearl-gemm C-ABI shim.
//
// All entry points return int status (0 = success, <0 = error). All raw
// pointers are device pointers (CUdeviceptr / nint).

using System.Runtime.InteropServices;

namespace Akoya.PearlGemm;

public static partial class PearlGemmNative
{
    public const string Lib = "pearl_gemm_capi";

    [LibraryImport(Lib, EntryPoint = "pearl_capi_abi_version")]
    public static partial int AbiVersion();

    [LibraryImport(Lib, EntryPoint = "pearl_capi_build_profile")]
    public static partial nint BuildProfilePtr();

    /// <summary>Select the noise-seed derivation for the salted-seed hardfork
    /// (pearl PR #280): 0 = V2 legacy, 1 = V3 dimension-bound roots.
    ///
    /// An export rather than a params-struct field, so it is additive and the
    /// pearl_capi ABI version does not move. A library built before the fork
    /// lacks the symbol and throws EntryPointNotFoundException — which the
    /// caller (SaltedSeedFork) catches and reports loudly, because the
    /// alternative is a host and GPU that silently disagree.</summary>
    [LibraryImport(Lib, EntryPoint = "pearl_capi_set_salted_seed")]
    public static partial void SetSaltedSeed(int on);

    [LibraryImport(Lib, EntryPoint = "pearl_capi_get_salted_seed")]
    public static partial int GetSaltedSeed();

    /// <summary>Runs the DEVICE noise-seed derivation — the same kernel the mining
    /// path uses — so a test can prove the GPU and the C# host agree. Pass
    /// stream = 0 to let it use its own queue. All buffers are 32 bytes.</summary>
    [LibraryImport(Lib, EntryPoint = "pearl_capi_derive_noise_seeds")]
    public static partial int DeriveNoiseSeedsDevice(
        ref byte aMerkleRoot, ref byte bMerkleRoot, ref byte jobKey,
        int m, int n, int salted,
        ref byte outASeed, ref byte outBSeed, nint stream);

    public static string BuildProfile()
        => Marshal.PtrToStringUTF8(BuildProfilePtr()) ?? "unknown";

    [LibraryImport(Lib, EntryPoint = "pearl_capi_target_family")]
    public static partial nint TargetFamilyPtr();

    /// <summary>GPU family this kernel was AOT-compiled for: "acm" (Alchemist),
    /// "bmg" (Battlemage), "fat" (one binary with both generations' AOT kernels,
    /// runs on any Arc), or "" (JIT — runs on any Arc). The wrong-card guard only
    /// fires for "acm"/"bmg". Older libs without the export are treated as JIT
    /// (empty).</summary>
    public static string TargetFamily()
    {
        try { return Marshal.PtrToStringUTF8(TargetFamilyPtr()) ?? ""; }
        catch (EntryPointNotFoundException) { return ""; }
    }

    [LibraryImport(Lib, EntryPoint = "pearl_capi_supports_sm")]
    public static partial int SupportsSm(int major, int minor);

    [LibraryImport(Lib, EntryPoint = "pearl_capi_get_host_signal_sync_size")]
    public static partial int GetHostSignalSyncSize();

    [LibraryImport(Lib, EntryPoint = "pearl_capi_get_host_signal_header_size")]
    public static partial int GetHostSignalHeaderSize();

    [LibraryImport(Lib, EntryPoint = "pearl_capi_get_required_scratchpad_bytes")]
    public static partial long GetRequiredScratchpadBytes(long matrixBytes, int threadsPerBlock);

    [LibraryImport(Lib, EntryPoint = "pearl_capi_search_m")]
    public static partial int SearchMNative(int m);

    /// <summary>The search-M window the kernel actually sweeps (== native
    /// compute_search_m). Used to size search-window-only device buffers (ApEA).
    /// Falls back to <paramref name="m"/> (full size — always safe) if the loaded
    /// lib predates the export.</summary>
    public static int SearchM(int m)
    {
        try { return SearchMNative(m); }
        catch (EntryPointNotFoundException) { return m; }
    }

    // Trigger-path fused A regen + leaf-CV export: regenerates A for (seedLo,
    // seedHi) in-register, writes only the first persistBytes (sm search rows) to
    // aOut, and produces the full-A leaf-CV table + Merkle root — no full-A buffer.
    // Lets the host keep the resident A buffer sized to the search window.
    [LibraryImport(Lib, EntryPoint = "pearl_capi_tensor_hash_fused_leaf_cvs")]
    public static partial int TensorHashFusedLeafCvs(
        nint aOut,
        ulong seedLo,
        ulong seedHi,
        long len,
        long persistBytes,
        nint key,
        nint roots,
        nint outHash,
        nint leafCvs,
        nint stream);

    [StructLayout(LayoutKind.Sequential)]
    public struct InstallBParams
    {
        public int M, N, K, R;
        public int ExpandBSeed;
        public uint ThNumBlocks;
        public uint ThThreads;
        public uint ThStages;
        public uint ThLeaves;
        public int DeviceId;

        public nint BSeed;
        public nint B;
        public nint BHash;
        public nint Key;
        public nint Roots;
        public nint AHash;
        public nint CommitA;
        public nint CommitB;
        public nint EAR_K_major;
        public nint EBL_R_major;
        public nint EBL_K_major;
        public nint EBR;
        public nint EBR_fp16;
        public nint EARxBpEB;
        public nint BpEB;
        public nint Workspace;
        public nint LeafCvs;

        // ABI v4: seed derivation for THIS σ (0 = legacy, 1 = salted/V3).
        // Mirrors `salted_seeds` as the LAST field of PearlCapiInstallBParams.
        // The install path bakes the B-side noise from this, once per σ — a
        // wrong value here is not corrected by anything downstream.
        public int SaltedSeeds;
    }

    [LibraryImport(Lib, EntryPoint = "pearl_capi_install_B")]
    public static unsafe partial int InstallB(InstallBParams* p, nint stream);

    // ABI v2: per-σ workspace pool. Allocate once after noise_gen at
    // σ-refresh, pass the handle through every NoiseB / NoisyGemm call, free
    // on σ-rotation. Saves the per-iter cudaMallocAsync/Free pair inside the
    // portable noisy_gemm path (measured ~+10 % on RTX 3080 / 5090).
    [LibraryImport(Lib, EntryPoint = "pearl_capi_workspace_alloc")]
    public static unsafe partial int WorkspaceAlloc(
        int m, int n, int k, int r,
        int withNoiseA, int withNoiseB,
        nint* outWorkspace, nint stream);

    [LibraryImport(Lib, EntryPoint = "pearl_capi_workspace_free")]
    public static partial int WorkspaceFree(nint workspace, nint stream);

    // Deterministic int7 ([-63, +63]) device fill, keyed by (seedLo, seedHi).
    // Host replay lives in Akoya.Crypto.LcgInt7 — both are byte-identical so
    // proof-time A recovery does not need to keep snapshot buffers around.
    [LibraryImport(Lib, EntryPoint = "pearl_capi_lcg_int7_fill")]
    public static partial int LcgInt7Fill(nint dst, long n, ulong seedLo, ulong seedHi, nint stream);

    // ── Per-σ constant cache — eliminates per-iter argument marshalling ──────
    //
    // Call WorkspaceInstallParams() ONCE after WorkspaceAlloc() and after all
    // device pointers are stable (i.e. at σ-install time). The workspace then
    // caches ALL constants so the per-iter hot path can use the minimal
    // Iter() call (4 args, 1 P/Invoke) instead of 5 calls × 40 args.
    //
    // WorkspaceParams mirrors PearlCapiWorkspaceParams in pearl_gemm_capi.h.
    // Must be [StructLayout(Sequential)] — passed by pointer to the C ABI.
    [StructLayout(LayoutKind.Sequential)]
    public struct WorkspaceParams
    {
        // Dimensions
        public int M, N, K, R;
        public int BM, BN, BK, CM, CN;

        // TensorHash constants (= TENSOR_HASH_THREADS/STAGES/LEAVES)
        public uint ThNumBlocks;   // = ceil(M*K / (ThThreads * 1024))
        public uint ThThreads;     // = 128
        public uint ThStages;      // = 2
        public uint ThLeaves;      // = 512

        // seed_hi for lcg_int7_fill (= σ seed, constant within σ lifetime)
        public ulong SigmaSeed;

        // Device pointers — content changes per-iter, pointer values are const
        public nint A, B, AHash, BHash, Key, Roots, CommitA, CommitB;
        public nint EAL, EAL_fp16, EBR, EBR_fp16;
        public nint EAR_R_major, EBL_R_major, EAR_K_major, EBL_K_major;
        public nint AxEBL_fp16, EARxBpEB_fp16;
        public nint ApEA, BpEB;
        public nint A_scales, B_scales, C;
        public nint HostSignalSync;   // device — dSync coordination block
        public nint PowTarget;        // device uint32[8]
        public nint PowKey;           // device uint32[8]

        // ABI v3: noise-seed derivation for THIS σ (0 = legacy raw roots,
        // 1 = salted/V3). Must sit immediately after PowKey — it mirrors
        // `salted_seeds` as the LAST field of PearlCapiWorkspaceParams, and
        // SyclKSub below is a host-only trailing field the native side has
        // never read. Inserting anything between PowKey and this breaks the
        // struct layout silently.
        public int SaltedSeeds;

        public int SyclKSub;          // SYCL systolic depth (16 or 32); host-side only
    }

    // Install constant per-σ params into the workspace.  Must be called before
    // the first Iter() call.  Safe to call again on σ-rotation.
    [LibraryImport(Lib, EntryPoint = "pearl_capi_workspace_install_params")]
    public static unsafe partial int WorkspaceInstallParams(nint workspace, WorkspaceParams* p);

    // Batched variant of Iter(): launches `count` consecutive nonces starting
    // at seedLoStart, using hostSignalHeaderPinnedBatch[i] as the pinned slot
    // for iter i. Reduces managed/native transition overhead in QueueBatch.
    [LibraryImport(Lib, EntryPoint = "pearl_capi_iter_batch")]
    public static unsafe partial int IterBatch(
        nint workspace,
        ulong seedLoStart,
        nint* hostSignalHeaderPinnedBatch,
        int count,
        nint stream);

    [LibraryImport(Lib, EntryPoint = "pearl_capi_iter_batch_graph_prepare")]
    public static unsafe partial int IterBatchGraphPrepare(
        nint workspace,
        nint* hostSignalHeaderPinnedBatch,
        int count,
        nint stream);

    [LibraryImport(Lib, EntryPoint = "pearl_capi_iter_batch_graph_launch")]
    public static partial int IterBatchGraphLaunch(
        nint workspace,
        ulong seedLoStart,
        nint stream);
}
