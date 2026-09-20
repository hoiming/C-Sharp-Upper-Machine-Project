using System.Collections.Concurrent;
using System.IO.Ports;
using EnvMonitor.Protocol;

namespace EnvMonitor.Communication;

public sealed class SerialFrameClient : ISerialFrameClient
{
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<Frame>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _sync = new();
    private SerialPort? _port;
    private Stream? _stream;
    private CancellationTokenSource? _receiveCancellation;
    private Task? _receiveLoop;
    private int _nextSequence;

    public bool IsConnected
    {
        get
        {
            lock (_sync)
            {
                return _port?.IsOpen == true;
            }
        }
    }

    public Task ConnectAsync(string portName, int baudRate, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(baudRate);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (_port is not null)
            {
                throw new InvalidOperationException("The serial client is already connected.");
            }

            var port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = SerialPort.InfiniteTimeout,
                WriteTimeout = SerialPort.InfiniteTimeout
            };
            port.Open();
            _port = port;
            _stream = port.BaseStream;
            _receiveCancellation = new CancellationTokenSource();
            _receiveLoop = ReceiveLoopAsync(_stream, _receiveCancellation.Token);
        }

        return Task.CompletedTask;
    }

    public async Task<Frame> SendAsync(
        byte command,
        ReadOnlyMemory<byte> payload,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);

        Stream stream;
        lock (_sync)
        {
            if (_stream is null || _port?.IsOpen != true)
            {
                throw new InvalidOperationException("The serial client is not connected.");
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
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            var timeoutTask = Task.Delay(timeoutMs, cancellationToken);
            var completed = await Task.WhenAny(completion.Task, timeoutTask).ConfigureAwait(false);
            if (completed == completion.Task)
            {
                return await completion.Task.ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"No serial response received for sequence {sequence}.");
        }
        finally
        {
            _pending.TryRemove(sequence, out _);
        }
    }

    public async Task DisconnectAsync()
    {
        CancellationTokenSource? cancellation;
        Task? receiveLoop;
        SerialPort? port;

        lock (_sync)
        {
            cancellation = _receiveCancellation;
            receiveLoop = _receiveLoop;
            port = _port;
            _receiveCancellation = null;
            _receiveLoop = null;
            _stream = null;
            _port = null;
        }

        cancellation?.Cancel();
        port?.Close();
        port?.Dispose();
        CompletePending(new ObjectDisposedException(nameof(SerialFrameClient)));

        if (receiveLoop is not null)
        {
            try
            {
                await receiveLoop.ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
        }

        cancellation?.Dispose();
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

    private async Task ReceiveLoopAsync(Stream stream, CancellationToken cancellationToken)
    {
        var parser = new FrameParser();
        var buffer = new byte[256];

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
                CompletePending(new IOException("The serial connection was closed."));
            }
        }
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