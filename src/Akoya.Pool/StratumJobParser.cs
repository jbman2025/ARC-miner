using System.Security.Cryptography;
using Akoya.Crypto;
using System.Text.Json;
using PearlPool.Proto.V2;

namespace Akoya.Pool;

public static class StratumJobParser
{
    public static JobAssignment ParseNotification(
        JsonElement arr,
        byte[] extranonce1,
        byte[] extranonce2)
    {
        // Stratum mining.notify params:
        // 0: job_id (string/hex)
        // 1: prevhash (string/hex, 32 B)
        // 2: coinb1 (string/hex)
        // 3: coinb2 (string/hex)
        // 4: merkle_branch (array of string/hex)
        // 5: version (string/hex, 4 B)
        // 6: nbits (string/hex, 4 B)
        // 7: ntime (string/hex, 4 B)
        // 8: clean_jobs (bool)
        // 9: b_seed (optional/extended, 32 B hex)
        // 10: audit_k (optional/extended, uint)

        var jobIdStr = arr[0].GetString() ?? "";
        var prevHashBytes = HexToBytes(arr[1].GetString() ?? "");
        var coinb1Bytes = HexToBytes(arr[2].GetString() ?? "");
        var coinb2Bytes = HexToBytes(arr[3].GetString() ?? "");
        var merkleBranch = arr[4];
        var versionBytes = HexToBytes(arr[5].GetString() ?? "");
        var nbitsBytes = HexToBytes(arr[6].GetString() ?? "");
        var ntimeBytes = HexToBytes(arr[7].GetString() ?? "");

        byte[] bSeed = new byte[32];
        if (arr.GetArrayLength() > 9)
        {
            var bSeedHex = arr[9].GetString();
            if (!string.IsNullOrEmpty(bSeedHex))
            {
                bSeed = HexToBytes(bSeedHex);
            }
        }

        uint auditK = 8;
        if (arr.GetArrayLength() > 10)
        {
            if (arr[10].ValueKind == JsonValueKind.Number)
            {
                auditK = arr[10].GetUInt32();
            }
        }

        // 1. Calculate Coinbase TX hash
        byte[] coinbaseTx = new byte[coinb1Bytes.Length + extranonce1.Length + extranonce2.Length + coinb2Bytes.Length];
        int offset = 0;
        Buffer.BlockCopy(coinb1Bytes, 0, coinbaseTx, offset, coinb1Bytes.Length); offset += coinb1Bytes.Length;
        Buffer.BlockCopy(extranonce1, 0, coinbaseTx, offset, extranonce1.Length); offset += extranonce1.Length;
        Buffer.BlockCopy(extranonce2, 0, coinbaseTx, offset, extranonce2.Length); offset += extranonce2.Length;
        Buffer.BlockCopy(coinb2Bytes, 0, coinbaseTx, offset, coinb2Bytes.Length);

        byte[] txHash = Sha2.Sha256d(coinbaseTx);

        // 2. Calculate Merkle Root
        byte[] merkleRoot = txHash;
        foreach (var branchElement in merkleBranch.EnumerateArray())
        {
            byte[] node = HexToBytes(branchElement.GetString() ?? "");
            byte[] concat = new byte[merkleRoot.Length + node.Length];
            Buffer.BlockCopy(merkleRoot, 0, concat, 0, merkleRoot.Length);
            Buffer.BlockCopy(node, 0, concat, merkleRoot.Length, node.Length);
            merkleRoot = Sha2.Sha256d(concat);
        }

        // 3. Assemble the 76-byte block header (sigma)
        // Format: version (4 B) + prev_block_hash (32 B) + merkle_root (32 B) + ntime (4 B) + nbits (4 B)
        byte[] sigma = new byte[76];
        int sigOffset = 0;
        
        Buffer.BlockCopy(versionBytes, 0, sigma, sigOffset, 4); sigOffset += 4;
        Buffer.BlockCopy(prevHashBytes, 0, sigma, sigOffset, 32); sigOffset += 32;
        Buffer.BlockCopy(merkleRoot, 0, sigma, sigOffset, 32); sigOffset += 32;
        Buffer.BlockCopy(ntimeBytes, 0, sigma, sigOffset, 4); sigOffset += 4;
        Buffer.BlockCopy(nbitsBytes, 0, sigma, sigOffset, 4);

        // Convert jobIdStr (hex/string) to 16-byte UUID representation
        byte[] jobIdBytes = new byte[16];
        if (Guid.TryParse(jobIdStr, out var parsedGuid))
        {
            jobIdBytes = parsedGuid.ToByteArray();
        }
        else
        {
            // SHA256 of string as a fallback for short job strings to yield a stable 16 B Guid
            byte[] rawJobBytes = System.Text.Encoding.UTF8.GetBytes(jobIdStr);
            byte[] sha256 = SHA256.HashData(rawJobBytes);
            Buffer.BlockCopy(sha256, 0, jobIdBytes, 0, 16);
        }

        uint targetNbits = BitConverter.ToUInt32(nbitsBytes, 0);
        if (BitConverter.IsLittleEndian)
        {
            // nbits on network block headers are big-endian in target conversions
            targetNbits = BinaryPrimitives.ReverseEndianness(targetNbits);
        }

        return new JobAssignment
        {
            JobId = Google.Protobuf.ByteString.CopyFrom(jobIdBytes),
            Sigma = Google.Protobuf.ByteString.CopyFrom(sigma),
            TargetNbits = targetNbits,
            NetworkTargetNbits = targetNbits,
            // Recovered from the coinbase (BIP34) rather than left at 0. This used
            // to be a hard-coded 0, which fed StratumNotifyParams.Height and made
            // SaltedSeedFork.IsActive() answer FALSE forever on this path — so a
            // pool that sends Bitcoin-style notifies pinned the miner to legacy V2
            // seed derivation and, past the salted-seed height, had every share
            // proved against the wrong noise field. Silently: SaltedSeedFork.Apply
            // early-returns before its warning when the state does not change.
            // 0 is still returned when the height cannot be read, which is exactly
            // the old behaviour — this can only improve on it, never regress.
            BlockHeight = TryParseBip34Height(coinb1Bytes),
            ProtocolVersion = 2,
            BSeed = Google.Protobuf.ByteString.CopyFrom(bSeed),
            AuditK = auditK
        };
    }

    /// <summary>
    /// Read the BIP34 block height out of a coinbase transaction's scriptSig.
    ///
    /// Since BIP34 the coinbase scriptSig MUST begin with a push of the block
    /// height as a minimally-encoded little-endian CScriptNum, so the height is
    /// available without asking the pool for it. It lives at the very start of the
    /// scriptSig, which means it is inside <c>coinb1</c> (the part before the
    /// extranonce splice) and is therefore always present here.
    ///
    /// Layout walked below:
    /// <code>
    ///   version(4) | in-count(1) | prev txid(32) | prev index(4) | scriptSig len(varint) | scriptSig...
    ///   scriptSig: [push-len n][n height bytes, little-endian]
    /// </code>
    ///
    /// Returns 0 for anything it cannot read confidently — a short buffer, a
    /// non-standard opcode, or an implausible height. 0 means "unknown", which
    /// every caller already treats as "not evidence of a fork state", so a weird
    /// or hostile coinbase degrades to today's behaviour instead of asserting a
    /// wrong height (which would be worse than none — it could flip a fork gate).
    /// </summary>
    internal static long TryParseBip34Height(ReadOnlySpan<byte> coinb1)
    {
        // version(4) + input count(1) + prev txid(32) + prev index(4)
        const int ScriptSigLenOffset = 4 + 1 + 32 + 4;
        if (coinb1.Length < ScriptSigLenOffset + 2) return 0;

        // scriptSig length varint. A coinbase scriptSig is capped at 100 bytes by
        // consensus, so anything needing the multi-byte varint forms (>= 0xFD) is
        // malformed — bail rather than guess at the encoding.
        byte scriptSigLen = coinb1[ScriptSigLenOffset];
        if (scriptSigLen is 0 or >= 0xFD) return 0;

        int pushOffset = ScriptSigLenOffset + 1;
        byte push = coinb1[pushOffset];

        // Direct push of 1..4 bytes (OP_PUSHBYTES_1..4). Heights past ~16.7M need
        // 4 bytes, and minimal CScriptNum encoding appends a 0x00 when the top bit
        // of the final byte is set — 4 covers both. Anything else is not BIP34.
        if (push is < 1 or > 4) return 0;
        if (pushOffset + 1 + push > coinb1.Length) return 0;
        if (push > scriptSigLen) return 0;

        long height = 0;
        for (int i = 0; i < push; i++)
            height |= (long)coinb1[pushOffset + 1 + i] << (8 * i);

        // Reject implausible values so a malformed coinbase cannot fake a height
        // large enough to trip a fork gate. ~100M blocks is centuries away.
        return height is > 0 and < 100_000_000 ? height : 0;
    }

    private static byte[] HexToBytes(string hex) => Akoya.Crypto.Hex.Decode(hex);
}

internal static class BinaryPrimitives
{
    public static uint ReverseEndianness(uint value)
    {
        return (value & 0x000000FFu) << 24 |
               (value & 0x0000FF00u) << 8 |
               (value & 0x00FF0000u) >> 8 |
               (value & 0xFF000000u) >> 24;
    }
}
