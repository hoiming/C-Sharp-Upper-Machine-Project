namespace EnvMonitor.Protocol;

public sealed class Frame
{
    public const byte HeaderFirstByte = 0xAA;
    public const byte HeaderSecondByte = 0x55;
    public const int MinimumLength = 9;
    public const int MaximumLength = 1024;
    private readonly byte[] _payload;

    public Frame(byte command, ushort sequence, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumLength - MinimumLength)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        Command = command;
        Sequence = sequence;
        _payload = payload.ToArray();
    }

    public byte Command { get; }

    public ushort Sequence { get; }

    public ReadOnlyMemory<byte> Payload => _payload;

    public byte[] Encode()
    {
        var length = MinimumLength + _payload.Length;
        var encoded = new byte[length];

        encoded[0] = HeaderFirstByte;
        encoded[1] = HeaderSecondByte;
        encoded[2] = (byte)(length >> 8);
        encoded[3] = (byte)length;
        encoded[4] = Command;
        encoded[5] = (byte)(Sequence >> 8);
        encoded[6] = (byte)Sequence;
        _payload.CopyTo(encoded, 7);

        var crc = Crc16.Compute(encoded.AsSpan(0, length - sizeof(ushort)));
        encoded[^2] = (byte)crc;
        encoded[^1] = (byte)(crc >> 8);

        return encoded;
    }

    public static Frame Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < MinimumLength || encoded.Length > MaximumLength)
        {
            throw new ArgumentException("Frame length is invalid.", nameof(encoded));
        }

        if (encoded[0] != HeaderFirstByte || encoded[1] != HeaderSecondByte)
        {
            throw new ArgumentException("Frame header is invalid.", nameof(encoded));
        }

        var declaredLength = (encoded[2] << 8) | encoded[3];
        if (declaredLength != encoded.Length)
        {
            throw new ArgumentException("Declared frame length does not match the buffer length.", nameof(encoded));
        }

        var expectedCrc = (ushort)(encoded[^2] | (encoded[^1] << 8));
        var actualCrc = Crc16.Compute(encoded[..^2]);
        if (actualCrc != expectedCrc)
        {
            throw new ArgumentException("Frame CRC is invalid.", nameof(encoded));
        }

        var sequence = (ushort)((encoded[5] << 8) | encoded[6]);
        return new Frame(encoded[4], sequence, encoded[7..^2]);
    }
}