using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using EnvMonitor.Protocol;

namespace EnvMonitor.Simulator;

public sealed class SimulatorServer : IAsyncDisposable
{
    private readonly int _requestedPort;
    private readonly FaultOptions _faultOptions;
    private readonly VirtualDevice _device;
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private int _responseCount;
    private TcpListener? _listener;
    private CancellationTokenSource? _shutdown;
    private Task? _acceptLoop;

    public SimulatorServer(int port = 9000, FaultOptions? faultOptions = null)
    {
        if (port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        _requestedPort = port;
        _faultOptions = faultOptions ?? new FaultOptions();
        _device = new VirtualDevice();
    }

    public int Port { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("The simulator is already running.");
        }

        _listener = new TcpListener(IPAddress.Loopback, _requestedPort);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = AcceptLoopAsync(_shutdown.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var shutdown = _shutdown;
        if (shutdown is null)
        {
            return;
        }

        shutdown.Cancel();
        _listener?.Stop();

        foreach (var client in _clients.Keys)
        {
            client.Close();
        }

        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
        }

        _listener = null;
        _shutdown = null;
        _acceptLoop = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (_listener is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _clients.TryAdd(client, 0);
                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var clientShutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var closeTask = CloseClientAfterDelayAsync(client, clientShutdown);

        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var parser = new FrameParser();
                var buffer = new byte[4096];

                while (!clientShutdown.IsCancellationRequested)
                {
                    var bytesRead = await stream.ReadAsync(buffer, clientShutdown.Token).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    var data = buffer[..bytesRead];
                    var responses = new List<byte[]>();
                    foreach (var frame in parser.Feed(data))
                    {
                        Console.WriteLine($"RX cmd=0x{frame.Command:X2} seq={frame.Sequence} payload={FormatBytes(frame.Payload.Span)}");
                        var response = _device.Handle(frame);
                        if (response is null)
                        {
                            continue;
                        }

                        var responseNumber = Interlocked.Increment(ref _responseCount);
                        if (_faultOptions.DropProbabilityPercent > 0 &&
                            Random.Shared.Next(100) < _faultOptions.DropProbabilityPercent)
                        {
                            Console.WriteLine($"DROP response #{responseNumber}");
                            continue;
                        }

                        if (_faultOptions.ResponseDelay > TimeSpan.Zero)
                        {
                            await Task.Delay(_faultOptions.ResponseDelay, clientShutdown.Token).ConfigureAwait(false);
                        }

                        var encoded = response.Encode();
                        if (responseNumber == _faultOptions.CrcErrorResponseNumber)
                        {
                            encoded[^1] ^= 0xFF;
                            Console.WriteLine($"CRC ERROR injected in response #{responseNumber}");
                        }

                        responses.Add(encoded);
                    }

                    if (_faultOptions.CoalesceResponses && responses.Count > 1)
                    {
                        await WriteResponseAsync(stream, responses.SelectMany(bytes => bytes).ToArray(), clientShutdown.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        foreach (var response in responses)
                        {
                            await WriteResponseAsync(stream, response, clientShutdown.Token).ConfigureAwait(false);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            _clients.TryRemove(client, out _);
        }

        clientShutdown.Cancel();
        await closeTask.ConfigureAwait(false);
    }

    private async Task WriteResponseAsync(NetworkStream stream, byte[] encoded, CancellationToken cancellationToken)
    {
        Console.WriteLine($"TX bytes={FormatBytes(encoded)}");

        if (_faultOptions.FragmentResponses && encoded.Length > 1)
        {
            var splitIndex = encoded.Length / 2;
            await stream.WriteAsync(encoded.AsMemory(0, splitIndex), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(encoded.AsMemory(splitIndex), cancellationToken).ConfigureAwait(false);
            return;
        }

        await stream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
    }

    private async Task CloseClientAfterDelayAsync(TcpClient client, CancellationTokenSource clientShutdown)
    {
        if (_faultOptions.CloseAfter <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            await Task.Delay(_faultOptions.CloseAfter, clientShutdown.Token).ConfigureAwait(false);
            if (!clientShutdown.IsCancellationRequested)
            {
                Console.WriteLine($"CLOSE client after {_faultOptions.CloseAfter.TotalSeconds:0.#}s");
                client.Close();
                clientShutdown.Cancel();
            }
        }
        catch (OperationCanceledException) when (clientShutdown.IsCancellationRequested)
        {
        }
    }

    private static string FormatBytes(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(bytes);
    }
}

public sealed record FaultOptions
{
    public int DropProbabilityPercent { get; init; }

    public TimeSpan ResponseDelay { get; init; }

    public TimeSpan CloseAfter { get; init; }

    public int CrcErrorResponseNumber { get; init; }

    public bool FragmentResponses { get; init; }

    public bool CoalesceResponses { get; init; }
}

public sealed class VirtualDevice
{
    private readonly object _sync = new();
    private short _temperatureTenths = 250;
    private ushort _humidityTenths = 480;
    private ushort _pressureTenths = 1013;
    private byte _relay;

    public Frame? Handle(Frame request)
    {
        lock (_sync)
        {
            return request.Command switch
            {
                0x01 => ReadSensors(request),
                0x02 => SetRelay(request),
                0x03 => Heartbeat(request),
                _ => Error(request, 0x01)
            };
        }
    }

    private Frame ReadSensors(Frame request)
    {
        if (request.Payload.Length != 0)
        {
            return Error(request, 0x02);
        }

        _temperatureTenths = (short)Math.Clamp(_temperatureTenths + Random.Shared.Next(-2, 3), 200, 300);
        _humidityTenths = (ushort)Math.Clamp(_humidityTenths + Random.Shared.Next(-3, 4), 300, 700);
        _pressureTenths = (ushort)Math.Clamp(_pressureTenths + Random.Shared.Next(-2, 3), 980, 1040);

        return new Frame(request.Command, request.Sequence, [
            (byte)(_temperatureTenths >> 8), (byte)_temperatureTenths,
            (byte)(_humidityTenths >> 8), (byte)_humidityTenths,
            (byte)(_pressureTenths >> 8), (byte)_pressureTenths,
            _relay
        ]);
    }

    private Frame SetRelay(Frame request)
    {
        if (request.Payload.Length != 2 || request.Payload.Span[0] != 1 || request.Payload.Span[1] > 1)
        {
            return Error(request, request.Payload.Length == 2 && request.Payload.Span[0] != 1 ? (byte)0x03 : (byte)0x02);
        }

        _relay = request.Payload.Span[1];
        return new Frame(request.Command, request.Sequence, [_relay]);
    }

    private static Frame Heartbeat(Frame request)
    {
        return request.Payload.Length == 0 ? new Frame(request.Command, request.Sequence, []) : Error(request, 0x02);
    }

    private static Frame Error(Frame request, byte errorCode)
    {
        return new Frame((byte)(request.Command | 0x80), request.Sequence, [errorCode]);
    }
}