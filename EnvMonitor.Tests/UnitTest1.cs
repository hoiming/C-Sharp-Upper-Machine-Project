using EnvMonitor.Protocol;
using EnvMonitor.Simulator;
using EnvMonitor.Communication;
using System.Net;
using System.Net.Sockets;

namespace EnvMonitor.Tests;

public class Crc16Tests
{
    [Fact]
    public void Compute_ReturnsModbusKnownValue()
    {
        var data = "123456789"u8;

        var crc = Crc16.Compute(data);

        Assert.Equal(0x4B37, crc);
    }
}

public class FrameTests
{
    [Fact]
    public void Encode_EmptyReadFrame_UsesExpectedLengthAndByteOrder()
    {
        var frame = new Frame(0x01, 1, []);

        var encoded = frame.Encode();

        Assert.Equal(9, encoded.Length);
        Assert.Equal(
            new byte[] { 0xAA, 0x55, 0x00, 0x09, 0x01, 0x00, 0x01, 0x8E, 0x93 },
            encoded);
    }

    [Fact]
    public void Decode_EncodedFrame_RoundTripsFields()
    {
        var original = new Frame(0x02, 0x1234, [0x01, 0x01]);

        var decoded = Frame.Decode(original.Encode());

        Assert.Equal(original.Command, decoded.Command);
        Assert.Equal(original.Sequence, decoded.Sequence);
        Assert.Equal(original.Payload.ToArray(), decoded.Payload.ToArray());
    }
}

public class FrameParserTests
{
    [Fact]
    public void Feed_CompleteFrame_ReturnsOneFrame()
    {
        var expected = new Frame(0x01, 1, []);
        var parser = new FrameParser();

        var frames = parser.Feed(expected.Encode()).ToArray();

        Assert.Single(frames);
        Assert.Equal(expected.Sequence, frames[0].Sequence);
    }

    [Fact]
    public void Feed_SplitFrame_ReturnsFrameOnSecondFeed()
    {
        var encoded = new Frame(0x01, 2, [0x10, 0x20]).Encode();
        var parser = new FrameParser();

        Assert.Empty(parser.Feed(encoded[..4]).ToArray());
        var frames = parser.Feed(encoded[4..]).ToArray();

        Assert.Single(frames);
        Assert.Equal(2, frames[0].Sequence);
    }

    [Fact]
    public void Feed_ConcatenatedFrames_ReturnsBothFrames()
    {
        var first = new Frame(0x01, 3, []).Encode();
        var second = new Frame(0x02, 4, [0x01, 0x01]).Encode();
        var parser = new FrameParser();

        var frames = parser.Feed(first.Concat(second).ToArray()).ToArray();

        Assert.Equal(2, frames.Length);
        Assert.Equal([3, 4], frames.Select(frame => frame.Sequence).ToArray());
    }

    [Fact]
    public void Feed_CrcError_DoesNotReturnFrame()
    {
        var encoded = new Frame(0x01, 5, []).Encode();
        encoded[^1] ^= 0xFF;
        var parser = new FrameParser();

        var frames = parser.Feed(encoded).ToArray();

        Assert.Empty(frames);
    }

    [Fact]
    public void Feed_GarbageBeforeFrame_SkipsGarbage()
    {
        var encoded = new Frame(0x01, 6, []).Encode();
        var parser = new FrameParser();

        var frames = parser.Feed([0x00, 0x12, 0xAA, 0x01, .. encoded]).ToArray();

        Assert.Single(frames);
        Assert.Equal(6, frames[0].Sequence);
    }

    [Fact]
    public void Feed_CrcErrorFollowedByValidFrame_Resynchronizes()
    {
        var invalid = new Frame(0x01, 7, []).Encode();
        invalid[^2] ^= 0xFF;
        var valid = new Frame(0x01, 8, []).Encode();
        var parser = new FrameParser();

        var frames = parser.Feed(invalid.Concat(valid).ToArray()).ToArray();

        var frame = Assert.Single(frames);
        Assert.Equal(8, frame.Sequence);
    }

    [Fact]
    public void Feed_InvalidLengthFollowedByValidFrame_Resynchronizes()
    {
        var valid = new Frame(0x01, 9, []).Encode();
        var invalidLength = new byte[] { 0xAA, 0x55, 0x00, 0x08 };
        var parser = new FrameParser();

        var frames = parser.Feed(invalidLength.Concat(valid).ToArray()).ToArray();

        var frame = Assert.Single(frames);
        Assert.Equal(9, frame.Sequence);
    }

    [Fact]
    public void Feed_SplitHeader_ReturnsFrameOnSecondFeed()
    {
        var encoded = new Frame(0x01, 10, []).Encode();
        var parser = new FrameParser();

        Assert.Empty(parser.Feed([0xAA]).ToArray());
        var frames = parser.Feed(encoded[1..]).ToArray();

        var frame = Assert.Single(frames);
        Assert.Equal(10, frame.Sequence);
    }
}

public class SimulatorIntegrationTests
{
    [Fact]
    public async Task ReadCommand_ReturnsSensorResponse()
    {
        await using var server = new SimulatorServer(0);
        await server.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);

        var request = new Frame(0x01, 42, []).Encode();
        await client.GetStream().WriteAsync(request);

        var parser = new FrameParser();
        var buffer = new byte[256];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Frame? response = null;

        while (response is null)
        {
            var bytesRead = await client.GetStream().ReadAsync(buffer, timeout.Token);
            Assert.NotEqual(0, bytesRead);
            response = parser.Feed(buffer[..bytesRead]).SingleOrDefault();
        }

        Assert.Equal(0x01, response.Command);
        Assert.Equal(42, response.Sequence);
        Assert.Equal(7, response.Payload.Length);
        Assert.InRange(ReadInt16(response.Payload.Span[0..2]) / 10.0, 20, 30);
        Assert.InRange(ReadUInt16(response.Payload.Span[2..4]) / 10.0, 30, 70);
        Assert.InRange(ReadUInt16(response.Payload.Span[4..6]) / 10.0, 98, 104);
        Assert.Equal(0, response.Payload.Span[6]);
    }

    [Fact]
    public async Task SetRelayAndHeartbeat_ReturnSuccessfulResponses()
    {
        await using var server = new SimulatorServer(0);
        await server.StartAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await client.GetStream().WriteAsync(new Frame(0x02, 1, [0x01, 0x01]).Encode(), timeout.Token);
        var relayResponse = await ReadFrameAsync(client, timeout.Token);
        Assert.Equal(0x02, relayResponse.Command);
        Assert.Equal(new byte[] { 0x01, 0x01 }, relayResponse.Payload.ToArray());

        await client.GetStream().WriteAsync(new Frame(0x03, 2, []).Encode(), timeout.Token);
        var heartbeatResponse = await ReadFrameAsync(client, timeout.Token);
        Assert.Equal(0x03, heartbeatResponse.Command);
        Assert.Empty(heartbeatResponse.Payload.ToArray());
    }

    [Fact]
    public async Task DropAllResponses_ProducesNoResponse()
    {
        await using var server = new SimulatorServer(0, new FaultOptions { DropProbabilityPercent = 100 });
        await server.StartAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await client.GetStream().WriteAsync(new Frame(0x01, 3, []).Encode(), timeout.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.GetStream().ReadAsync(new byte[64], timeout.Token));
    }

    [Fact]
    public async Task CrcErrorOption_ProducesFrameThatParserRejects()
    {
        await using var server = new SimulatorServer(0, new FaultOptions { CrcErrorResponseNumber = 1 });
        await server.StartAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await client.GetStream().WriteAsync(new Frame(0x01, 4, []).Encode(), timeout.Token);
        var buffer = new byte[256];
        var bytesRead = await client.GetStream().ReadAsync(buffer, timeout.Token);
        var parser = new FrameParser();

        Assert.Empty(parser.Feed(buffer[..bytesRead]).ToArray());
    }

    [Fact]
    public async Task FragmentOption_ClientStillReceivesResponse()
    {
        await using var server = new SimulatorServer(0, new FaultOptions { FragmentResponses = true });
        await server.StartAsync();
        await using var client = new TcpClientService();
        await client.ConnectAsync("127.0.0.1", server.Port);

        var response = await client.SendAsync(0x01, ReadOnlyMemory<byte>.Empty, 1000);

        Assert.Equal(0x01, response.Command);
        Assert.Equal(7, response.Payload.Length);
    }

    [Fact]
    public async Task CoalesceOption_ParserReturnsBothResponses()
    {
        await using var server = new SimulatorServer(0, new FaultOptions { CoalesceResponses = true });
        await server.StartAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var requests = new Frame(0x01, 20, []).Encode()
            .Concat(new Frame(0x01, 21, []).Encode())
            .ToArray();
        await client.GetStream().WriteAsync(requests, timeout.Token);

        var parser = new FrameParser();
        var buffer = new byte[512];
        var frames = new List<Frame>();
        while (frames.Count < 2)
        {
            var bytesRead = await client.GetStream().ReadAsync(buffer, timeout.Token);
            Assert.NotEqual(0, bytesRead);
            frames.AddRange(parser.Feed(buffer[..bytesRead]));
        }

        Assert.Equal([20, 21], frames.Select(frame => frame.Sequence).ToArray());
    }

    [Fact]
    public async Task UnknownCommand_ReturnsUnsupportedErrorFrame()
    {
        await using var server = new SimulatorServer(0);
        await server.StartAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await client.GetStream().WriteAsync(new Frame(0xFF, 22, []).Encode(), timeout.Token);
        var response = await ReadFrameAsync(client, timeout.Token);

        Assert.Equal(0xFF, response.Command);
        Assert.Equal(22, response.Sequence);
        Assert.Equal(new byte[] { 0x01 }, response.Payload.ToArray());
    }

    private static async Task<Frame> ReadFrameAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var parser = new FrameParser();
        var buffer = new byte[256];

        while (true)
        {
            var bytesRead = await client.GetStream().ReadAsync(buffer, cancellationToken);
            Assert.NotEqual(0, bytesRead);
            var frame = parser.Feed(buffer[..bytesRead]).SingleOrDefault();
            if (frame is not null)
            {
                return frame;
            }
        }
    }

    private static short ReadInt16(ReadOnlySpan<byte> bytes)
    {
        return (short)((bytes[0] << 8) | bytes[1]);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes)
    {
        return (ushort)((bytes[0] << 8) | bytes[1]);
    }
}

public class TcpClientServiceTests
{
    [Fact]
    public async Task SendAsync_ReadCommand_ReturnsMatchedResponse()
    {
        await using var server = new SimulatorServer(0);
        await server.StartAsync();
        await using var client = new TcpClientService();
        await client.ConnectAsync("127.0.0.1", server.Port);

        var response = await client.SendAsync(0x01, ReadOnlyMemory<byte>.Empty, 1000);

        Assert.Equal(0x01, response.Command);
        Assert.Equal(7, response.Payload.Length);
        Assert.Equal(0, client.PendingCount);
    }

    [Fact]
    public async Task SendAsync_ConcurrentRequests_MatchesEveryResponse()
    {
        await using var server = new SimulatorServer(0);
        await server.StartAsync();
        await using var client = new TcpClientService();
        await client.ConnectAsync("127.0.0.1", server.Port);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => client.SendAsync(0x01, ReadOnlyMemory<byte>.Empty, 2000)));

        Assert.Equal(10, responses.Length);
        Assert.All(responses, response => Assert.Equal(0x01, response.Command));
        Assert.Equal(0, client.PendingCount);
    }

    [Fact]
    public async Task SendAsync_DroppedResponse_ThrowsTimeoutAndCleansPending()
    {
        await using var server = new SimulatorServer(0, new FaultOptions { DropProbabilityPercent = 100 });
        await server.StartAsync();
        await using var client = new TcpClientService();
        await client.ConnectAsync("127.0.0.1", server.Port);

        await Assert.ThrowsAsync<TimeoutException>(() => client.SendAsync(0x01, ReadOnlyMemory<byte>.Empty, 100));

        Assert.Equal(0, client.PendingCount);
    }

    [Fact]
    public async Task SendAsync_DelayedResponse_ThrowsTimeoutAndCleansPending()
    {
        await using var server = new SimulatorServer(0, new FaultOptions { ResponseDelay = TimeSpan.FromMilliseconds(250) });
        await server.StartAsync();
        await using var client = new TcpClientService();
        await client.ConnectAsync("127.0.0.1", server.Port);

        await Assert.ThrowsAsync<TimeoutException>(() => client.SendAsync(0x01, ReadOnlyMemory<byte>.Empty, 50));

        Assert.Equal(0, client.PendingCount);
    }

    [Fact]
    public async Task SendAsync_CrcErrorResponse_ThrowsTimeout()
    {
        await using var server = new SimulatorServer(0, new FaultOptions { CrcErrorResponseNumber = 1 });
        await server.StartAsync();
        await using var client = new TcpClientService();
        await client.ConnectAsync("127.0.0.1", server.Port);

        await Assert.ThrowsAsync<TimeoutException>(() => client.SendAsync(0x01, ReadOnlyMemory<byte>.Empty, 100));

        Assert.Equal(0, client.PendingCount);
    }

    [Fact]
    public async Task DisconnectAsync_CompletesPendingRequest()
    {
        await using var server = new SimulatorServer(0, new FaultOptions { DropProbabilityPercent = 100 });
        await server.StartAsync();
        await using var client = new TcpClientService();
        await client.ConnectAsync("127.0.0.1", server.Port);

        var pending = client.SendAsync(0x01, ReadOnlyMemory<byte>.Empty, 5000);
        await Task.Delay(50);
        await client.DisconnectAsync();

        await Assert.ThrowsAnyAsync<ObjectDisposedException>(() => pending);
        Assert.Equal(0, client.PendingCount);
    }

    [Fact]
    public async Task UnexpectedDisconnect_AutomaticallyReconnects()
    {
        await using var server = new SimulatorServer(0, new FaultOptions { CloseAfter = TimeSpan.FromMilliseconds(100) });
        await server.StartAsync();
        await using var client = new TcpClientService();
        var connectedAgain = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectedCount = 0;
        client.ConnectionChanged += state =>
        {
            if (state == ConnectionState.Connected && Interlocked.Increment(ref connectedCount) > 1)
            {
                connectedAgain.TrySetResult(true);
            }
        };

        await client.ConnectAsync("127.0.0.1", server.Port);
        var completed = await Task.WhenAny(connectedAgain.Task, Task.Delay(TimeSpan.FromSeconds(4)));

        Assert.Same(connectedAgain.Task, completed);
        Assert.True(connectedCount >= 2);
    }

    [Fact]
    public async Task ManualDisconnect_DoesNotStartReconnectLoop()
    {
        await using var server = new SimulatorServer(0);
        await server.StartAsync();
        await using var client = new TcpClientService();
        var connectedCount = 0;
        client.ConnectionChanged += state =>
        {
            if (state == ConnectionState.Connected)
            {
                Interlocked.Increment(ref connectedCount);
            }
        };

        await client.ConnectAsync("127.0.0.1", server.Port);
        await client.DisconnectAsync();
        await Task.Delay(1200);

        Assert.Equal(1, connectedCount);
    }
}

public class DeviceServiceTests
{
    [Fact]
    public async Task ReadAsync_ParsesBigEndianScaledSensorValues()
    {
        var client = new FakeFrameClient((command, _, _, _) =>
            Task.FromResult(new Frame(command, 1, [0xFF, 0x9C, 0x01, 0xF4, 0x03, 0xF5, 0x01])));
        var service = new DeviceService(client);

        var data = await service.ReadAsync();

        Assert.Equal(-10.0, data.Temp);
        Assert.Equal(50.0, data.Humidity);
        Assert.Equal(101.3, data.Pressure);
        Assert.Equal(1, data.Relay);
    }

    [Fact]
    public async Task SetRelayAsync_SendsExpectedPayloadAndValidatesResponse()
    {
        ReadOnlyMemory<byte> sentPayload = default;
        var client = new FakeFrameClient((command, payload, _, _) =>
        {
            sentPayload = payload;
            return Task.FromResult(new Frame(command, 1, [0x01, 0x01]));
        });
        var service = new DeviceService(client);

        await service.SetRelayAsync(1, true);

        Assert.Equal(new byte[] { 0x01, 0x01 }, sentPayload.ToArray());
    }

    [Fact]
    public async Task ReadAsync_InvalidPayloadLength_ThrowsInvalidDataException()
    {
        var client = new FakeFrameClient((command, _, _, _) =>
            Task.FromResult(new Frame(command, 1, [0x00, 0xFA])));
        var service = new DeviceService(client);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadAsync());
    }

    [Fact]
    public async Task HeartbeatAsync_ErrorResponse_ThrowsDeviceError()
    {
        var client = new FakeFrameClient((_, _, _, _) =>
            Task.FromResult(new Frame(0x83, 1, [0x01])));
        var service = new DeviceService(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.HeartbeatAsync());
    }
}

file sealed class FakeFrameClient : IFrameClient
{
    private readonly Func<byte, ReadOnlyMemory<byte>, int, CancellationToken, Task<Frame>> _send;

    public FakeFrameClient(Func<byte, ReadOnlyMemory<byte>, int, CancellationToken, Task<Frame>> send)
    {
        _send = send;
    }

    public int PendingCount => 0;

    public event Action<ConnectionState>? ConnectionChanged
    {
        add { }
        remove { }
    }

    public event Action<Frame>? UnsolicitedFrame
    {
        add { }
        remove { }
    }

    public Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<Frame> SendAsync(byte command, ReadOnlyMemory<byte> payload, int timeoutMs, CancellationToken cancellationToken = default)
        => _send(command, payload, timeoutMs, cancellationToken);

    public Task DisconnectAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class LedServiceTests
{
    [Fact]
    public async Task SetLedAsync_SendsLedCommandAndReturnsState()
    {
        ReadOnlyMemory<byte> sentPayload = default;
        var client = new FakeSerialFrameClient((command, payload, _, _) =>
        {
            sentPayload = payload;
            return Task.FromResult(new Frame(command, 1, [0x01, 0x01]));
        });
        var service = new LedService(client);

        var state = await service.SetLedAsync(1, true);

        Assert.True(state);
        Assert.Equal(new byte[] { 0x01, 0x01 }, sentPayload.ToArray());
    }

    [Fact]
    public async Task GetLedAsync_ErrorResponse_ThrowsDeviceError()
    {
        var client = new FakeSerialFrameClient((_, _, _, _) =>
            Task.FromResult(new Frame(0x91, 1, [0x03])));
        var service = new LedService(client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetLedAsync(1));
    }
}

file sealed class FakeSerialFrameClient : ISerialFrameClient
{
    private readonly Func<byte, ReadOnlyMemory<byte>, int, CancellationToken, Task<Frame>> _send;

    public FakeSerialFrameClient(Func<byte, ReadOnlyMemory<byte>, int, CancellationToken, Task<Frame>> send)
    {
        _send = send;
    }

    public bool IsConnected => true;

    public Task ConnectAsync(string portName, int baudRate, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<Frame> SendAsync(byte command, ReadOnlyMemory<byte> payload, int timeoutMs, CancellationToken cancellationToken = default)
        => _send(command, payload, timeoutMs, cancellationToken);

    public Task DisconnectAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}