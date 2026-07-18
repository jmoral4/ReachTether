using ReachTether.Audio;
using ReachTether.Audio.Alsa;

internal interface IRealtimeAudioOutput
{
    void Begin(AudioFormat sourceFormat);
    void Write(byte[] pcmChunk, CancellationToken cancellationToken);
    void Complete();
    void Cancel();
}

internal sealed class LocalRealtimeAudioOutput(LocalAudioSession audioSession) : IRealtimeAudioOutput
{
    public void Begin(AudioFormat sourceFormat) => audioSession.BeginPlaybackStream(sourceFormat);

    public void Write(byte[] pcmChunk, CancellationToken cancellationToken)
        => audioSession.WritePlaybackPcm16Chunk(pcmChunk, cancellationToken);

    public void Complete() => audioSession.CompletePlaybackStream();

    public void Cancel() => audioSession.CancelPlaybackStream();
}
