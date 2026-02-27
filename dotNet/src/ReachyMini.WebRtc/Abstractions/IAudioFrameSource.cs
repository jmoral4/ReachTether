using ReachyMini.Audio;

namespace ReachyMini.WebRtc.Abstractions;

public interface IAudioFrameSource
{
    ValueTask<AudioFrame?> ReadAsync(CancellationToken cancellationToken = default);
}
