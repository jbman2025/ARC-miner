using System.Text.Json.Serialization;

namespace Akoya.Pool;

public sealed class StratumRequest
{
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public object[]? Params { get; set; }
}

public sealed class StratumMessage
{
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("params")]
    public System.Text.Json.JsonElement? Params { get; set; }

    [JsonPropertyName("result")]
    public System.Text.Json.JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    public System.Text.Json.JsonElement? Error { get; set; }
}

public sealed class StratumShareSubmission
{
    [JsonPropertyName("sigma")]
    public string Sigma { get; set; } = string.Empty;
    
    [JsonPropertyName("config_bytes")]
    public string ConfigBytes { get; set; } = string.Empty;
    
    [JsonPropertyName("hash_a")]
    public string HashA { get; set; } = string.Empty;
    
    [JsonPropertyName("hash_b")]
    public string HashB { get; set; } = string.Empty;
    
    [JsonPropertyName("a_slice")]
    public string ASlice { get; set; } = string.Empty;
    
    [JsonPropertyName("b_slice")]
    public string BSlice { get; set; } = string.Empty;
    
    [JsonPropertyName("claimed_hash")]
    public string ClaimedHash { get; set; } = string.Empty;
    
    [JsonPropertyName("tile_row")]
    public int TileRow { get; set; }
    
    [JsonPropertyName("tile_col")]
    public int TileCol { get; set; }
}

public sealed class StratumAuthorizeRequest
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = "mining.authorize";

    [JsonPropertyName("params")]
    public StratumAuthorizeParams Params { get; set; } = new();
}

public sealed class StratumAuthorizeParams
{
    [JsonPropertyName("wallet")]
    public string Wallet { get; set; } = string.Empty;

    [JsonPropertyName("worker")]
    public string Worker { get; set; } = string.Empty;

    [JsonPropertyName("agent")]
    public string Agent { get; set; } = string.Empty;
}

public sealed class StratumNotifyParams
{
    [JsonPropertyName("job_id")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("header")]
    public string Header { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    [JsonPropertyName("height")]
    public long Height { get; set; }
}

public sealed class StratumSubmitRequest
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = "mining.submit";

    [JsonPropertyName("params")]
    public StratumSubmitParams Params { get; set; } = new();
}

public sealed class StratumSubmitParams
{
    [JsonPropertyName("job_id")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("plain_proof")]
    public string PlainProof { get; set; } = string.Empty;
}

[JsonSerializable(typeof(StratumRequest))]
[JsonSerializable(typeof(StratumMessage))]
[JsonSerializable(typeof(StratumShareSubmission))]
[JsonSerializable(typeof(StratumAuthorizeRequest))]
[JsonSerializable(typeof(StratumAuthorizeParams))]
[JsonSerializable(typeof(StratumNotifyParams))]
[JsonSerializable(typeof(StratumSubmitRequest))]
[JsonSerializable(typeof(StratumSubmitParams))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(object[]))]
internal partial class StratumJsonContext : JsonSerializerContext
{
}
