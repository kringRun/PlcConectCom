using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PlcConectCom.WinFormsTest;

// Socket服务端通讯服务（单客户端优先）
public sealed class SocketServerCommunicationService : IDisposable
{
    private TcpListener? _listener;
    private readonly ConcurrentDictionary<string, TcpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _runCts;

    public event Action<string>? ClientConnected;

    public event Action<string>? ClientDisconnected;

    public event Action<string, string>? DataReceived;

    public bool IsRunning => _listener is not null;

    public async Task StartAsync(int port, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
            return;

        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();

        _ = Task.Run(async () => await AcceptLoopAsync(_runCts.Token), _runCts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch
            {
                break;
            }

            string endpoint = client.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString("N");
            _clients[endpoint] = client;
            ClientConnected?.Invoke(endpoint);
            _ = Task.Run(async () => await ReceiveLoopAsync(endpoint, client, cancellationToken), cancellationToken);
        }
    }

    private async Task ReceiveLoopAsync(string endpoint, TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            NetworkStream stream = client.GetStream();
            var buffer = new byte[4096];
            while (!cancellationToken.IsCancellationRequested && client.Connected)
            {
                int read = await stream.ReadAsync(buffer, cancellationToken);
                if (read <= 0)
                    break;

                string text = Encoding.UTF8.GetString(buffer, 0, read);
                DataReceived?.Invoke(endpoint, text);
            }
        }
        catch
        {
            // ignore read exceptions in background loop
        }
        finally
        {
            if (_clients.TryRemove(endpoint, out TcpClient? removed))
            {
                try
                {
                    removed.Close();
                    removed.Dispose();
                }
                catch
                {
                    // ignore
                }
            }

            ClientDisconnected?.Invoke(endpoint);
        }
    }

    public IReadOnlyList<string> GetClientEndpoints()
    {
        return _clients.Keys.ToList();
    }

    public async Task SendAsync(string endpoint, string data, Encoding? encoding = null, CancellationToken cancellationToken = default)
    {
        TcpClient client = GetClient(endpoint);
        byte[] bytes = (encoding ?? Encoding.UTF8).GetBytes(data);
        await client.GetStream().WriteAsync(bytes, cancellationToken);
    }

    public async Task<string> ReceiveAsync(string endpoint, int bufferSize = 4096, int timeoutMs = 3000, Encoding? encoding = null, CancellationToken cancellationToken = default)
    {
        TcpClient client = GetClient(endpoint);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeoutMs);

        byte[] buffer = new byte[Math.Max(256, bufferSize)];
        int read = await client.GetStream().ReadAsync(buffer, linkedCts.Token);
        if (read <= 0)
            return string.Empty;

        return (encoding ?? Encoding.UTF8).GetString(buffer, 0, read);
    }

    public void Stop()
    {
        try
        {
            _runCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        foreach (var kv in _clients)
        {
            try
            {
                kv.Value.Close();
                kv.Value.Dispose();
            }
            catch
            {
                // ignore
            }
        }

        _clients.Clear();

        try
        {
            _listener?.Stop();
        }
        catch
        {
            // ignore
        }
        finally
        {
            _listener = null;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private TcpClient GetClient(string endpoint)
    {
        if (!_clients.TryGetValue(endpoint, out TcpClient? client) || client is null || !client.Connected)
            throw new InvalidOperationException($"Socket客户端[{endpoint}]未连接。");

        return client;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
