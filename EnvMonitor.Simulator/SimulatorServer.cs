using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using EnvMonitor.Protocol;

namespace EnvMonitor.Simulator;

public sealed class SimulatorServer : IAsyncDisposable
{
    private readonly int _requestedPort;
    private readonly VirtualDevice _device = new();
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _shutdown;
    private Task? _acceptLoop;

    public SimulatorServer(int port = 9000)
    {
        if (port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        _requestedPort = port;
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
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var parser = new FrameParser();
                var buffer = new byte[4096];

                while (!cancellationToken.IsCancellationRequested)
                {
                    var bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    var data = buffer[..bytesRead];
                    foreach (var frame in parser.Feed(data))
                    {
                        var response = _device.Handle(frame);
                        if (response is null)
                        {
                            continue;
                        }

                        var encoded = response.Encode();
                        await stream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
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
    }
}

public sealed class VirtualDevice
{
    public Frame? Handle(Frame request)
    {
        if (request.Command == 0x01 && request.Payload.Length == 0)
        {
            return new Frame(request.Command, request.Sequence, [
                0x00, 0xFA,
                0x01, 0xE0,
                0x03, 0xF5,
                0x00
            ]);
        }

        return new Frame((byte)(request.Command | 0x80), request.Sequence, [0x01]);
    }
}