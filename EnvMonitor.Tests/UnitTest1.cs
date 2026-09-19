using EnvMonitor.Protocol;

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