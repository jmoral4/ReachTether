using ReachyMini.WebRtc.Models;

namespace ReachyMini.WebRtc.Abstractions;

public interface ISignalingClient : IAsyncDisposable
{
    event Action<WebRtcSignalingMessage>? MessageReceived;

    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task SendAsync(WebRtcSignalingMessage message, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
