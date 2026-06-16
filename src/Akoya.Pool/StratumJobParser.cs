using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using PearlPool.Proto.V2;

namespace Akoya.Pool;

public static class StratumJobParser
{
    public static JobAssignment ParseNotification(
        JsonElement.ArrayEnumerator paramsArray,
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

        var jobIdStr = paramsArray.Current.GetString() ?? "";
        paramsArray.MoveNext();

        var prevHashBytes = HexToBytes(paramsArray.Current.GetString() ?? "");
        paramsArray.MoveNext();

        var coinb1Bytes = HexToBytes(paramsArray.Current.GetString() ?? "");
        paramsArray.MoveNext();

        var coinb2Bytes = HexToBytes(paramsArray.Current.GetString() ?? "");
        paramsArray.MoveNext();

        var merkleBranch = paramsArray.Current;
        paramsArray.MoveNext();

        var versionBytes = HexToBytes(paramsArray.Current.GetString() ?? "");
        paramsArray.MoveNext();

        var nbitsBytes = HexToBytes(paramsArray.Current.GetString() ?? "");
        paramsArray.MoveNext();

        var ntimeBytes = HexToBytes(paramsArray.Current.GetString() ?? "");
        paramsArray.MoveNext();

        // Skip clean_jobs (bool)
        paramsArray.MoveNext();

        // Default or extended parameters for Pearl network
        byte[] bSeed = new byte[32];
        if (paramsArray.Current.ValueKind != JsonValueKind.Undefined)
        {
            var bSeedHex = paramsArray.Current.GetString();
            if (!string.IsNullOrEmpty(bSeedHex))
            {
                bSeed = HexToBytes(bSeedHex);
            }
        }
        paramsArray.MoveNext();

        uint auditK = 8;
        if (paramsArray.Current.ValueKind != JsonValueKind.Undefined)
        {
            auditK = paramsArray.Current.GetUInt32();
        }

        // 1. Calculate Coinbase TX hash
        byte[] coinbaseTx = new byte[coinb1Bytes.Length + extranonce1.Length + extranonce2.Length + coinb2Bytes.Length];
        int offset = 0;
        Buffer.BlockCopy(coinb1Bytes, 0, coinbaseTx, offset, coinb1Bytes.Length); offset += coinb1Bytes.Length;
        Buffer.BlockCopy(extranonce1, 0, coinbaseTx, offset, extranonce1.Length); offset += extranonce1.Length;
        Buffer.BlockCopy(extranonce2, 0, coinbaseTx, offset, extranonce2.Length); offset += extranonce2.Length;
        Buffer.BlockCopy(coinb2Bytes, 0, coinbaseTx, offset, coinb2Bytes.Length);

        byte[] txHash = SHA256.HashData(SHA256.HashData(coinbaseTx));

        // 2. Calculate Merkle Root
        byte[] merkleRoot = txHash;
        foreach (var branchElement in merkleBranch.EnumerateArray())
        {
            byte[] node = HexToBytes(branchElement.GetString() ?? "");
            byte[] concat = new byte[merkleRoot.Length + node.Length];
            Buffer.BlockCopy(merkleRoot, 0, concat, 0, merkleRoot.Length);
            Buffer.BlockCopy(node, 0, concat, merkleRoot.Length, node.Length);
            merkleRoot = SHA256.HashData(SHA256.HashData(concat));
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
            BlockHeight = 0,
            ProtocolVersion = 2,
            BSeed = Google.Protobuf.ByteString.CopyFrom(bSeed),
            AuditK = auditK
        };
    }

    private static byte[] HexToBytes(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return [];
        if (hex.Length % 2 != 0) hex = "0" + hex;
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        }
        return bytes;
    }
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
