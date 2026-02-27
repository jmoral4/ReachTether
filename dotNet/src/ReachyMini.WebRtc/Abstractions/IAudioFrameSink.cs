using ReachyMini.Audio;

namespace ReachyMini.WebRtc.Abstractions;

public interface IAudioFrameSink
{
    Task WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default);
}
