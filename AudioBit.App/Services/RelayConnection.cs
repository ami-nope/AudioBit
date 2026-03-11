using System.Net.WebSockets;
using System.Text;
using System.IO;

namespace AudioBit.App.Services;

internal sealed class RelayConnection : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private Uri _endpoint;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _socketGate = new();

    private ClientWebSocket? _socket;
    private bool _disposed;

    public RelayConnection(Uri endpoint, Action<string> log)
    {
        _endpoint = endpoint;
        _log = log;
    }

    public event Action? Connected;

    public event Action<string>? Disconnected;

    public event Action<string>? MessageReceived;

    public bool IsConnected
    {
        get
        {
            var socket = _socket;
            return socket is not null && socket.State == WebSocketState.Open;
        }
    }

    public Uri Endpoint
    {
        get
        {
            lock (_socketGate)
            {
                return _endpoint;
            }
        }
    }

    public void SetEndpoint(Uri endpoint)
    {
        ThrowIfDisposed();

        lock (_socketGate)
        {
            _endpoint = endpoint;
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await CloseAsync(CancellationToken.None).ConfigureAwait(false);

        Uri endpoint;
        lock (_socketGate)
        {
            endpoint = _endpoint;
        }

        var socket = new ClientWebSocket
        {
            Options =
            {
                KeepAliveInterval = TimeSpan.FromSeconds(20),
            },
        };

        using var connectTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeoutCts.CancelAfter(ConnectTimeout);
        try
        {
            await socket.ConnectAsync(endpoint, connectTimeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw new TimeoutException($"Timed out connecting to relay endpoint '{endpoint}'.");
        }

        lock (_socketGate)
        {
            _socket = socket;
        }

        _log($"PC connected to relay: {endpoint}");
        Connected?.Invoke();
    }

    public async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null)
        {
            return;
        }

        var buffer = new byte[8192];
        var segment = new ArraySegment<byte>(buffer);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await socket.ReceiveAsync(segment, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await NotifyDisconnectedAsync("Relay closed the socket.").ConfigureAwait(false);
                        return;
                    }

                    stream.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                var message = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
                MessageReceived?.Invoke(message);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown/reconnect
        }
        catch (Exception ex)
        {
            await NotifyDisconnectedAsync($"Receive loop failed: {ex.Message}").ConfigureAwait(false);
        }
    }

    public async Task SendJsonAsync(string json, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        var payload = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var socket = _socket;
            if (socket is null || socket.State != WebSocketState.Open)
            {
                return;
            }

            await socket.SendAsync(
                    new ArraySegment<byte>(payload),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            await NotifyDisconnectedAsync($"Send failed: {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        ClientWebSocket? socketToClose;
        lock (_socketGate)
        {
            socketToClose = _socket;
            _socket = null;
        }

        if (socketToClose is null)
        {
            return;
        }

        try
        {
            if (socketToClose.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socketToClose.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Closing",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // swallow close errors
        }
        finally
        {
            socketToClose.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sendLock.Dispose();
        CloseAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private async Task NotifyDisconnectedAsync(string reason)
    {
        _log($"Connection lost: {reason}");
        Disconnected?.Invoke(reason);
        await CloseAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
