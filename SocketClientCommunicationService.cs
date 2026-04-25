using System.Net.Sockets;
using System.Text;

namespace PlcConectCom.WinFormsTest;

// Socket客户端通讯服务
public sealed class SocketClientCommunicationService : IDisposable
{
    private TcpClient? _client;

    public bool IsConnected => _client is { Connected: true };

    public async Task ConnectAsync(string host, int port, int timeoutMs = 3000)
    {
        DisposeClient();
        _client = new TcpClient();

        using var cts = new CancellationTokenSource(timeoutMs);
        await _client.ConnectAsync(host, port, cts.Token);
    }

    public async Task SendAsync(string data, Encoding? encoding = null, CancellationToken cancellationToken = default)
    {
        var stream = GetStream();
        byte[] bytes = (encoding ?? Encoding.UTF8).GetBytes(data);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    public async Task<string> ReceiveAsync(int bufferSize = 4096, int timeoutMs = 3000, Encoding? encoding = null, CancellationToken cancellationToken = default)
    {
        var stream = GetStream();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeoutMs);

        byte[] buffer = new byte[Math.Max(256, bufferSize)];
        int read = await stream.ReadAsync(buffer, linkedCts.Token);
        if (read <= 0)
            return string.Empty;

        return (encoding ?? Encoding.UTF8).GetString(buffer, 0, read);
    }

    public void Disconnect()
    {
        DisposeClient();
    }

    private NetworkStream GetStream()
    {
        if (_client is null || !_client.Connected)
            throw new InvalidOperationException("Socket客户端未连接。");

        return _client.GetStream();
    }

    private void DisposeClient()
    {
        try
        {
            _client?.Close();
            _client?.Dispose();
        }
        catch
        {
            // ignore
        }
        finally
        {
            _client = null;
        }
    }

    public void Dispose()
    {
        DisposeClient();
        GC.SuppressFinalize(this);
    }
}
