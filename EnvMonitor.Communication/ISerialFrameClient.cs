using EnvMonitor.Protocol;

namespace EnvMonitor.Communication;

public interface ISerialFrameClient : IAsyncDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(string portName, int baudRate, CancellationToken cancellationToken = default);

    Task<Frame> SendAsync(
        byte command,
        ReadOnlyMemory<byte> payload,
        int timeoutMs,
        CancellationToken cancellationToken = default);

    Task DisconnectAsync();
}