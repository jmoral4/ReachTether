using System.Text.Json.Nodes;
using ReachyMini.Audio;
using ReachyMini.WebRtc.Models;

namespace ReachyMini.WebRtc.Abstractions;

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
