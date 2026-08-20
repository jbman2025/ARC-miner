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

    [JsonPropertyName("type")]
    public string? Type { get; set; }
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

    [JsonPropertyName("type")]
    public string? Type { get; set; }
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

    [JsonPropertyName("b_seed")]
    public string? BSeed { get; set; }

    [JsonPropertyName("audit_k")]
    public uint? AuditK { get; set; }
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
