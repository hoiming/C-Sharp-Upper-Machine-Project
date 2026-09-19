using EnvMonitor.Protocol;

namespace EnvMonitor.Communication;

public sealed record SensorData(double Temp, double Humidity, double Pressure, byte Relay);

public interface IDeviceService
{
    Task<SensorData> ReadAsync(CancellationToken cancellationToken = default);

    Task SetRelayAsync(byte channel, bool on, CancellationToken cancellationToken = default);

    Task HeartbeatAsync(CancellationToken cancellationToken = default);
}

public sealed class DeviceService : IDeviceService
{
    private const byte ReadCommand = 0x01;
    private const byte SetRelayCommand = 0x02;
    private const byte HeartbeatCommand = 0x03;
    private readonly IFrameClient _client;
    private readonly int _timeoutMs;

    public DeviceService(IFrameClient client, int timeoutMs = 1000)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
        _timeoutMs = timeoutMs;
    }

    public async Task<SensorData> ReadAsync(CancellationToken cancellationToken = default)
    {
        var response = await _client.SendAsync(ReadCommand, ReadOnlyMemory<byte>.Empty, _timeoutMs, cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response, ReadCommand);

        var payload = response.Payload.ToArray();
        if (payload.Length != 7)
        {
            throw new InvalidDataException($"Read response payload must be 7 bytes, but was {payload.Length}.");
        }

        return new SensorData(
            ReadInt16(payload[0..2]) / 10.0,
            ReadUInt16(payload[2..4]) / 10.0,
            ReadUInt16(payload[4..6]) / 10.0,
            payload[6]);
    }

    public async Task SetRelayAsync(byte channel, bool on, CancellationToken cancellationToken = default)
    {
        var response = await _client.SendAsync(
                SetRelayCommand,
                new byte[] { channel, on ? (byte)1 : (byte)0 },
                _timeoutMs,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response, SetRelayCommand);

        var payload = response.Payload.ToArray();
        if (payload.Length != 2 || payload[0] != channel || payload[1] > 1)
        {
            throw new InvalidDataException("Set relay response payload is invalid.");
        }

        if (payload[1] != (on ? 1 : 0))
        {
            throw new InvalidDataException("Set relay response does not match the requested state.");
        }
    }

    public async Task HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        var response = await _client.SendAsync(HeartbeatCommand, ReadOnlyMemory<byte>.Empty, _timeoutMs, cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response, HeartbeatCommand);

        if (response.Payload.Length != 0)
        {
            throw new InvalidDataException("Heartbeat response payload must be empty.");
        }
    }

    private static void EnsureSuccess(Frame response, byte expectedCommand)
    {
        if (response.Command == expectedCommand)
        {
            return;
        }

        if (response.Command == (expectedCommand | 0x80) && response.Payload.Length == 1)
        {
            throw new InvalidOperationException($"Device rejected command 0x{expectedCommand:X2}; error code 0x{response.Payload.Span[0]:X2}.");
        }

        throw new InvalidDataException($"Unexpected response command 0x{response.Command:X2} for 0x{expectedCommand:X2}.");
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