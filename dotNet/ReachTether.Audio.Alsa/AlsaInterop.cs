using System.Runtime.InteropServices;

namespace ReachTether.Audio.Alsa;

internal static partial class AlsaInterop
{
    private const string LibAsound = "libasound.so.2";

    public const int SND_PCM_STREAM_PLAYBACK = 0;
    public const int SND_PCM_STREAM_CAPTURE = 1;

    public const int SND_PCM_FORMAT_S16_LE = 2;

    public const int SND_PCM_ACCESS_RW_INTERLEAVED = 3;

    [DllImport(LibAsound, EntryPoint = "snd_pcm_open")]
    public static extern int Open(out nint pcm, string name, int stream, int mode);

    [DllImport(LibAsound, EntryPoint = "snd_pcm_close")]
    public static extern int Close(nint pcm);

    [DllImport(LibAsound, EntryPoint = "snd_pcm_prepare")]
    public static extern int Prepare(nint pcm);

    [DllImport(LibAsound, EntryPoint = "snd_pcm_drop")]
    public static extern int Drop(nint pcm);

    [DllImport(LibAsound, EntryPoint = "snd_pcm_drain")]
    public static extern int Drain(nint pcm);

    [DllImport(LibAsound, EntryPoint = "snd_pcm_recover")]
    public static extern int Recover(nint pcm, int err, int silent);

    [DllImport(LibAsound, EntryPoint = "snd_pcm_set_params")]
    public static extern int SetParams(
        nint pcm,
        int format,
        int access,
        uint channels,
        uint rate,
        int softResample,
        uint latencyUs);

    [DllImport(LibAsound, EntryPoint = "snd_pcm_readi")]
    public static extern nint ReadInterleaved(nint pcm, nint buffer, nuint frames);

    [DllImport(LibAsound, EntryPoint = "snd_pcm_writei")]
    public static extern nint WriteInterleaved(nint pcm, nint buffer, nuint frames);

    [DllImport(LibAsound, EntryPoint = "snd_strerror")]
    private static extern nint StrErrorPtr(int errnum);

    public static string StrError(int errnum)
    {
        var ptr = StrErrorPtr(errnum);
        return Marshal.PtrToStringAnsi(ptr) ?? $"ALSA error {errnum}";
    }

    [DllImport(LibAsound, EntryPoint = "snd_pcm_state")]
    public static extern int State(nint pcm);

    [DllImport(LibAsound, EntryPoint = "snd_pcm_avail")]
    public static extern nint Avail(nint pcm);
}
