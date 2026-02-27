namespace ReachyMini.Audio.Alsa;

public sealed class LocalAudioOptions
{
    public string CaptureDevice { get; set; } = "reachymini_audio_src";

    public string PlaybackDevice { get; set; } = "reachymini_audio_sink";

    public uint SampleRate { get; set; } = 16000;

    public uint Channels { get; set; } = 2;

    public uint LatencyUs { get; set; } = 100_000;

    public int ReadChunkMs { get; set; } = 50;

    public int WriteChunkMs { get; set; } = 50;
}
