using ReachTether.WebRtc.Models;

namespace ReachTether.WebRtc.Abstractions;

public interface ISignalingClient : IAsyncDisposable
{
    event Action<WebRtcSignalingMessage>? MessageReceived;

    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task SendAsync(WebRtcSignalingMessage message, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
