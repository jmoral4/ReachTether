using System.Text.Json.Nodes;
using ReachTether.Audio;
using ReachTether.WebRtc.Models;

namespace ReachTether.WebRtc.Abstractions;

public interface IReachySession : IAsyncDisposable
{
    ReachySessionState State { get; }
    string CorrelationId { get; }

    event Action<ReachySessionState>? StateChanged;

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task SendCommandAsync(JsonObject command, CancellationToken cancellationToken = default);
    Task<AudioFrame[]> CaptureFramesAsync(TimeSpan duration, CancellationToken cancellationToken = default);
    Task PlayWaveAsync(byte[] wavBytes, CancellationToken cancellationToken = default);
}
