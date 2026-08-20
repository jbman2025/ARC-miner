// Hex encode/decode shared by every stratum dialect.
//
// Before this existed there were FIVE copies with THREE different behaviours:
//
//   CsdHash.Unhex / GrHash.Unhex          Substring-per-byte loop, no guards.
//                                         Odd-length input SILENTLY TRUNCATED
//                                         (new byte[s.Length/2] drops the last
//                                         nibble) — a wrong-magnitude target
//                                         that no test would catch.
//   Pool/MiningSession.HexToBytes         Convert.FromHexString — THROWS on odd.
//   Pool/StratumJobParser.HexToBytes      empty -> [], odd -> left-pad "0".
//   Pool/StratumSession.HexToBytes        (same as StratumJobParser)
//
// Decode adopts the left-pad semantics, which is the only one that is correct
// for a hex-encoded NUMBER: "abc" means 0x0abc, not 0xab. That makes this a
// behaviour change for the csd/gr and MiningSession call sites, but only on
// odd-length input, which is a malformed frame in every stratum dialect we
// speak — previously it silently corrupted or threw, now it parses.

namespace Akoya.Crypto;

public static class Hex
{
    /// <summary>Hex string -> bytes. Null/empty yields an empty array; an
    /// odd-length string is left-padded with '0' so its magnitude is preserved
    /// ("abc" -> 0x0a 0xbc). Throws <see cref="FormatException"/> on non-hex
    /// characters.</summary>
    public static byte[] Decode(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return [];
        if ((hex.Length & 1) != 0) hex = "0" + hex;
        return Convert.FromHexString(hex);
    }

    /// <summary>Bytes -> lowercase hex. Pools are case-insensitive on the wire,
    /// but lowercase is what every reference miner sends and what the accepted-
    /// share captures in the tests were taken from.</summary>
    public static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);
}
