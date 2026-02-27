namespace ReachTether.Audio;

public sealed class AudioFrame
{
    public AudioFrame(byte[] pcm16Bytes, AudioFormat format, long timestampMsUtc)
    {
        Pcm16Bytes = pcm16Bytes ?? throw new ArgumentNullException(nameof(pcm16Bytes));
        Format = format;
        TimestampMsUtc = timestampMsUtc;
    }

    public byte[] Pcm16Bytes { get; }
    public AudioFormat Format { get; }
    public long TimestampMsUtc { get; }
}
