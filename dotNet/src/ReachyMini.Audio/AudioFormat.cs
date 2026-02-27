namespace ReachyMini.Audio;

public readonly record struct AudioFormat(int SampleRateHz, short Channels, short BitsPerSample)
{
    public static readonly AudioFormat Pcm16Mono16k = new(16000, 1, 16);
    public static readonly AudioFormat Pcm16Mono24k = new(24000, 1, 16);

    public int BytesPerSample => BitsPerSample / 8;
    public int BlockAlign => Channels * BytesPerSample;
    public long BytesPerSecond => (long)SampleRateHz * BlockAlign;
}
