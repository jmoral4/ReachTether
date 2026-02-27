using ReachTether.Audio;

namespace ReachTether.WebRtc.Abstractions;

public interface IAudioFrameSink
{
    Task WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default);
}
