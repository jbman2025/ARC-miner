using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

#pragma warning disable CA5359 // Modify SslStream validation callback

namespace Akoya.Pool;

public sealed class StratumConnection : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly bool _useTls;
    private readonly bool _tlsInsecure;
    private readonly ILogger<StratumConnection> _log;
    
    private TcpClient? _tcpClient;
    private Stream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Task? _readLoopTask;
    private readonly CancellationTokenSource _cts = new();

    public StratumConnection(string host, int port, bool useTls, bool tlsInsecure, ILogger<StratumConnection> log)
    {
        _host = host;
        _port = port;
        _useTls = useTls;
        _tlsInsecure = tlsInsecure;
        _log = log;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _log.LogInformation("stratum: connecting to {Host}:{Port} (tls={Tls})", _host, _port, _useTls);
        _tcpClient = new TcpClient();
        await _tcpClient.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
        
        Stream networkStream = _tcpClient.GetStream();
        if (_useTls)
        {
            RemoteCertificateValidationCallback? certCallback = null;
            if (_tlsInsecure)
            {
                certCallback = (_, _, _, _) => true;
            }

            var sslStream = new SslStream(networkStream, leaveInnerStreamOpen: false, certCallback);
            await sslStream.AuthenticateAsClientAsync(_host).ConfigureAwait(false);
            _stream = sslStream;
        }
        else
        {
            _stream = networkStream;
        }

        _reader = new StreamReader(_stream, Encoding.UTF8);
        _writer = new StreamWriter(_stream, Encoding.UTF8) { AutoFlush = true };
        _log.LogInformation("stratum: connected to {Host}:{Port}", _host, _port);
        
        _readLoopTask = Task.Run(ReadLoopAsync, CancellationToken.None);
    }

    public async Task SendRequestAsync(string method, object[] parameters, int id, CancellationToken ct)
    {
        if (_writer == null) throw new InvalidOperationException("Not connected");
        
        var request = new StratumRequest
        {
            Id = id,
            Method = method,
            Params = parameters
        };

        var json = JsonSerializer.Serialize(request, StratumJsonContext.Default.StratumRequest);
        _log.LogDebug("stratum: sending request: {Json}", json);
        await _writer.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested && _reader != null)
            {
                var line = await _reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line == null)
                {
                    _log.LogWarning("stratum: connection closed by remote pool");
                    break;
                }

                _log.LogDebug("stratum: received message: {Line}", line);
                try
                {
                    var msg = JsonSerializer.Deserialize(line, StratumJsonContext.Default.StratumMessage);
                    if (msg != null)
                    {
                        if (msg.Method != null)
                        {
                            _log.LogInformation("stratum: received notification: {Method}", msg.Method);
                        }
                        else
                        {
                            _log.LogInformation("stratum: received response id={Id}", msg.Id);
                        }
                    }
                }
                catch (JsonException ex)
                {
                    _log.LogError(ex, "stratum: failed to parse JSON-RPC message");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "stratum: read loop exception");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_readLoopTask != null)
        {
            try { await _readLoopTask.ConfigureAwait(false); } catch { /* ignore */ }
        }

        _reader?.Dispose();
        _writer?.Dispose();
        _stream?.Dispose();
        _tcpClient?.Dispose();
        _cts.Dispose();
    }
}
