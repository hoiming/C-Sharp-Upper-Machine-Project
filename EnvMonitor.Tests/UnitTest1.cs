using EnvMonitor.Protocol;
using EnvMonitor.Simulator;
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
        Assert.Equal(new byte[] { 0x00, 0xFA, 0x01, 0xE0, 0x03, 0xF5, 0x00 }, response.Payload.ToArray());
    }
}