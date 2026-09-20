using EnvMonitor.Protocol;

namespace EnvMonitor.Communication;

public interface ILedService
{
    Task<bool> SetLedAsync(byte ledId, bool enabled, CancellationToken cancellationToken = default);

    Task<bool> GetLedAsync(byte ledId, CancellationToken cancellationToken = default);
}

public sealed class LedService : ILedService
{
    private readonly ISerialFrameClient _client;
    private readonly int _timeoutMs;

    public LedService(ISerialFrameClient client, int timeoutMs = 1000)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
        _timeoutMs = timeoutMs;
    }

    public async Task<bool> SetLedAsync(byte ledId, bool enabled, CancellationToken cancellationToken = default)
    {
        var response = await _client.SendAsync(
            0x10,
            new byte[] { ledId, enabled ? (byte)1 : (byte)0 },
            _timeoutMs,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, 0x10);

        var payload = response.Payload.ToArray();
        if (payload.Length != 2 || payload[0] != ledId || payload[1] > 1)
        {
            throw new InvalidDataException("LED response payload is invalid.");
        }

        return payload[1] == 1;
    }

    public async Task<bool> GetLedAsync(byte ledId, CancellationToken cancellationToken = default)
    {
        var response = await _client.SendAsync(0x11, new byte[] { ledId }, _timeoutMs, cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response, 0x11);

        var payload = response.Payload.ToArray();
        if (payload.Length != 2 || payload[0] != ledId || payload[1] > 1)
        {
            throw new InvalidDataException("LED response payload is invalid.");
        }

        return payload[1] == 1;
    }

    private static void EnsureSuccess(Frame response, byte expectedCommand)
    {
        if (response.Command == expectedCommand)
        {
            return;
        }

        if (response.Command == (byte)(expectedCommand | 0x80) && response.Payload.Length == 1)
        {
            throw new InvalidOperationException($"STM32 rejected LED command; error code 0x{response.Payload.Span[0]:X2}.");
        }

        throw new InvalidDataException($"Unexpected LED response command 0x{response.Command:X2}.");
    }
}