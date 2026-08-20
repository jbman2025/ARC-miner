// Monero daemon JSON-RPC client (get_block_template / submit_block / get_info)
// for solo mining directly against a monerod node. JSON-RPC 2.0 with an object
// params map — distinct from the Bitcoin-style 1.0/array RPC used by BTX.

using System.Text;
using System.Text.Json;

namespace Akoya.Miner.Algos.Rx;

internal sealed class RxRpcClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _url;

    public RxRpcClient(string nodeHostPort, TimeSpan timeout)
    {
        // Accept host:port, or a full http(s):// URL with or without /json_rpc.
        string url = nodeHostPort;
        if (!url.Contains("://")) url = "http://" + url;
        if (!url.Contains("/json_rpc", StringComparison.Ordinal))
            url = url.TrimEnd('/') + "/json_rpc";
        _url = new Uri(url);
        _http = new HttpClient { Timeout = timeout };
    }

    /// <summary>One JSON-RPC 2.0 call. <paramref name="paramsJson"/> is the raw
    /// params object (e.g. <c>{"wallet_address":"…","reserve_size":8}</c>), or
    /// null for none. Returns the parsed "result" (caller disposes).</summary>
    public async Task<JsonDocument> CallAsync(string method, string? paramsJson, CancellationToken ct)
    {
        var body = paramsJson is null
            ? $"{{\"jsonrpc\":\"2.0\",\"id\":\"arc-rx\",\"method\":\"{method}\"}}"
            : $"{{\"jsonrpc\":\"2.0\",\"id\":\"arc-rx\",\"method\":\"{method}\",\"params\":{paramsJson}}}";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(_url, content, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"RPC {method} HTTP {(int)resp.StatusCode}: {Trim(text)}");

        var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
        {
            var msg = err.GetRawText();
            doc.Dispose();
            throw new InvalidOperationException($"RPC {method} error: {msg}");
        }
        if (!doc.RootElement.TryGetProperty("result", out _))
        {
            doc.Dispose();
            throw new InvalidOperationException($"RPC {method}: no result in response: {Trim(text)}");
        }
        return doc;
    }

    private static string Trim(string s) => s[..Math.Min(s.Length, 300)];

    public void Dispose() => _http.Dispose();
}
