// JSON emission for stratum params, AOT-safe.
//
// Each pool client used to carry its own copy of these helpers plus its own
// JsonSerializerContext (RxJsonContext, BtxJsonContext, NmJsonContext, …). That
// duplication is where the AOT `JsonSerializer` throw came from: reflection-based
// serialisation trims away under PublishAot, and it only surfaces at runtime, on
// the first share submit, against a live pool.
//
// One source-generated context, used by everyone. Escaping goes through
// System.Text.Json rather than hand-rolled string concatenation, because a
// worker name or password containing a quote or backslash would otherwise emit
// a frame the pool rejects — or worse, silently mis-parses.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Akoya.Miner.Mining.Stratum;

[JsonSerializable(typeof(string))]
internal sealed partial class StratumJsonContext : JsonSerializerContext;

internal static class StratumJson
{
    /// <summary>A properly escaped, quoted JSON string.</summary>
    public static string Str(string s) => JsonSerializer.Serialize(s, StratumJsonContext.Default.String);

    /// <summary>A JSON object with string values, in the given order. Order is
    /// preserved because some pools are picky about the login object's shape.</summary>
    public static string Obj(params (string Key, string Value)[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var sb = new StringBuilder("{");
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Str(fields[i].Key)).Append(':').Append(Str(fields[i].Value));
        }
        return sb.Append('}').ToString();
    }

    /// <summary>A JSON array of already-serialised values. Callers pass
    /// <see cref="Str"/> output for strings and bare literals for numbers/bools.</summary>
    public static string RawArray(params string[] rawValues)
    {
        ArgumentNullException.ThrowIfNull(rawValues);
        return "[" + string.Join(",", rawValues) + "]";
    }

    /// <summary>A JSON array of strings, each escaped.</summary>
    public static string StrArray(params string[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sb = new StringBuilder("[");
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Str(values[i]));
        }
        return sb.Append(']').ToString();
    }
}
