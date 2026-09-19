using EnvMonitor.Protocol;

namespace EnvMonitor.Communication;

public enum ConnectionState
{
    Connecting,
    Connected,
    Disconnected,
    Reconnecting
}

public interface IFrameClient : IAsyncDisposable
{
    int PendingCount { get; }

    event Action<ConnectionState>? ConnectionChanged;

    event Action<Frame>? UnsolicitedFrame;

    Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default);

    Task<Frame> SendAsync(
        byte command,
        ReadOnlyMemory<byte> payload,
        int timeoutMs,
        CancellationToken cancellationToken = default);

    Task DisconnectAsync();
}