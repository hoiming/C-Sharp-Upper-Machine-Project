using System.Collections.Concurrent;
using System.Net.Sockets;
using EnvMonitor.Protocol;

namespace EnvMonitor.Communication;

public sealed class TcpClientService : IFrameClient
{
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<Frame>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _sync = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _receiveCancellation;
    private Task? _receiveLoop;
    private CancellationTokenSource? _reconnectCancellation;
    private Task? _reconnectLoop;
    private string? _host;
    private int _port;
    private int _nextSequence;
    private bool _disconnecting;

    public int PendingCount => _pending.Count;

    public event Action<ConnectionState>? ConnectionChanged;

    public event Action<Frame>? UnsolicitedFrame;

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        lock (_sync)
        {
            if (_client is not null)
            {
                throw new InvalidOperationException("The client is already connected.");
            }

            _disconnecting = false;
            _host = host;
            _port = port;
            _reconnectCancellation?.Cancel();
            _reconnectCancellation?.Dispose();
            _reconnectCancellation = new CancellationTokenSource();
        }

        RaiseConnectionChanged(ConnectionState.Connecting);
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            var stream = client.GetStream();
            var receiveCancellation = new CancellationTokenSource();

            lock (_sync)
            {
                _client = client;
                _stream = stream;
                _receiveCancellation = receiveCancellation;
                _receiveLoop = ReceiveLoopAsync(stream, receiveCancellation.Token);
            }

            RaiseConnectionChanged(ConnectionState.Connected);
        }
        catch
        {
            client.Dispose();
            RaiseConnectionChanged(ConnectionState.Disconnected);
            throw;
        }
    }

    public async Task<Frame> SendAsync(
        byte command,
        ReadOnlyMemory<byte> payload,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);

        NetworkStream stream;
        lock (_sync)
        {
            if (_stream is null || _disconnecting)
            {
                throw new InvalidOperationException("The client is not connected.");
            }

            stream = _stream;
        }

        var sequence = ReserveSequence(out var completion);
        var encoded = new Frame(command, sequence, payload.Span).Encode();

        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            var timeoutTask = Task.Delay(timeoutMs, cancellationToken);
            var completedTask = await Task.WhenAny(completion.Task, timeoutTask).ConfigureAwait(false);
            if (completedTask == completion.Task)
            {
                return await completion.Task.ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"No response received for sequence {sequence} within {timeoutMs} ms.");
        }
        catch
        {
            _pending.TryRemove(sequence, out _);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        Task? receiveLoop;
        TcpClient? client;
        CancellationTokenSource? receiveCancellation;

        lock (_sync)
        {
            _disconnecting = true;
            _reconnectCancellation?.Cancel();
            _reconnectLoop = null;
            receiveLoop = _receiveLoop;
            client = _client;
            receiveCancellation = _receiveCancellation;
            _receiveLoop = null;
            _client = null;
            _stream = null;
            _receiveCancellation = null;
        }

        receiveCancellation?.Cancel();
        client?.Close();
        CompletePending(new ObjectDisposedException(nameof(TcpClientService)));

        if (receiveLoop is not null)
        {
            await receiveLoop.ConfigureAwait(false);
        }

        receiveCancellation?.Dispose();
        client?.Dispose();
        RaiseConnectionChanged(ConnectionState.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }

    private ushort ReserveSequence(out TaskCompletionSource<Frame> completion)
    {
        completion = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);

        for (var attempt = 0; attempt <= ushort.MaxValue; attempt++)
        {
            var sequence = unchecked((ushort)Interlocked.Increment(ref _nextSequence));
            if (_pending.TryAdd(sequence, completion))
            {
                return sequence;
            }
        }

        throw new InvalidOperationException("No request sequence is available.");
    }

    private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var parser = new FrameParser();
        var buffer = new byte[4096];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                foreach (var frame in parser.Feed(buffer[..bytesRead]))
                {
                    if (_pending.TryRemove(frame.Sequence, out var completion))
                    {
                        completion.TrySetResult(frame);
                    }
                    else
                    {
                        UnsolicitedFrame?.Invoke(frame);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            CompletePending(exception);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await HandleUnexpectedDisconnectAsync(stream).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleUnexpectedDisconnectAsync(NetworkStream stream)
    {
        TcpClient? clientToClose = null;
        CancellationTokenSource? receiveCancellation = null;
        CancellationToken reconnectToken;
        var shouldReconnect = false;

        lock (_sync)
        {
            if (ReferenceEquals(_stream, stream) && !_disconnecting)
            {
                clientToClose = _client;
                receiveCancellation = _receiveCancellation;
                _client = null;
                _stream = null;
                _receiveLoop = null;
                _receiveCancellation = null;
                shouldReconnect = _reconnectCancellation is not null;
                reconnectToken = _reconnectCancellation?.Token ?? CancellationToken.None;
            }
            else
            {
                reconnectToken = CancellationToken.None;
            }
        }

        if (!shouldReconnect)
        {
            return;
        }

        clientToClose?.Dispose();
        receiveCancellation?.Dispose();
        CompletePending(new IOException("The TCP connection was closed."));
        RaiseConnectionChanged(ConnectionState.Disconnected);

        lock (_sync)
        {
            if (_reconnectLoop is null || _reconnectLoop.IsCompleted)
            {
                _reconnectLoop = ReconnectLoopAsync(reconnectToken);
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                RaiseConnectionChanged(ConnectionState.Reconnecting);

                string host;
                int port;
                lock (_sync)
                {
                    host = _host ?? throw new InvalidOperationException("Reconnect target is missing.");
                    port = _port;
                }

                var client = new TcpClient();
                await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
                var stream = client.GetStream();
                var receiveCancellation = new CancellationTokenSource();

                lock (_sync)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        receiveCancellation.Cancel();
                        receiveCancellation.Dispose();
                        client.Dispose();
                        return;
                    }

                    _client = client;
                    _stream = stream;
                    _receiveCancellation = receiveCancellation;
                    _receiveLoop = ReceiveLoopAsync(stream, receiveCancellation.Token);
                }

                RaiseConnectionChanged(ConnectionState.Connected);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }

    private void RaiseConnectionChanged(ConnectionState state)
    {
        ConnectionChanged?.Invoke(state);
    }

    private void CompletePending(Exception exception)
    {
        foreach (var pair in _pending.ToArray())
        {
            if (_pending.TryRemove(pair.Key, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }
}