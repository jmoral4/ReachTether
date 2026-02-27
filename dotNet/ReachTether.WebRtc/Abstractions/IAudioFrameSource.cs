using ReachTether.Audio;

namespace ReachTether.WebRtc.Abstractions;

public interface IAudioFrameSource
{
    ValueTask<AudioFrame?> ReadAsync(CancellationToken cancellationToken = default);
}
