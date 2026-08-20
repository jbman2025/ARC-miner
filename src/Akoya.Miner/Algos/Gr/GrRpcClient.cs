// Bitcoin-style JSON-RPC client for a raptoreumd node (getblocktemplate /
// submitblock solo mining). Basic auth from user:pass or a .cookie file. Kept in
// the gr module (rather than reusing BTX's) so the algo stays self-contained.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Akoya.Miner.Algos.Gr;

internal sealed class GrRpcClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _url;

    public GrRpcClient(string url, string user, string password, TimeSpan timeout)
    {
        if (!url.Contains("://")) url = "http://" + url;
        _url = new Uri(url);
        _http = new HttpClient { Timeout = timeout };
        if (!string.IsNullOrEmpty(user) || !string.IsNullOrEmpty(password))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
    }

    /// <summary>Reads "user:pass" from a Bitcoin-style .cookie file.</summary>
    public static (string User, string Password) ReadCookie(string path)
    {
        var parts = File.ReadAllText(path).Trim().Split(':', 2);
        if (parts.Length != 2) throw new InvalidOperationException($"malformed RPC cookie file: {path}");
        return (parts[0], parts[1]);
    }

    /// <summary>One JSON-RPC 1.0 call. <paramref name="paramsJson"/> is the raw
    /// params array (e.g. <c>[{"rules":["segwit"]}]</c>); null means <c>[]</c>.
    /// Returns the parsed document (caller disposes).</summary>
    public async Task<JsonDocument> CallAsync(string method, string? paramsJson, CancellationToken ct)
    {
        var body = $"{{\"jsonrpc\":\"1.0\",\"id\":\"akoya-gr\",\"method\":\"{method}\",\"params\":{paramsJson ?? "[]"}}}";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(_url, content, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"RPC {method} HTTP {(int)resp.StatusCode}: {text[..Math.Min(text.Length, 300)]}");

        var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
        {
            var msg = err.GetRawText();
            doc.Dispose();
            throw new InvalidOperationException($"RPC {method} error: {msg}");
        }
        return doc;
    }

    public void Dispose() => _http.Dispose();
}
