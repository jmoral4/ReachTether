using System.Runtime.InteropServices;

namespace ReachTether.Audio.Alsa;

public sealed class AlsaPcmDevice : IDisposable
{
    private nint _pcm;
    private readonly string _deviceName;
    private readonly bool _isCapture;
    private bool _disposed;
    private byte[] _readScratch = Array.Empty<byte>();

    public uint SampleRate { get; }
    public uint Channels { get; }
    public int BytesPerFrame => (int)Channels * 2;

    private AlsaPcmDevice(nint pcm, string deviceName, bool isCapture, uint sampleRate, uint channels)
    {
        _pcm = pcm;
        _deviceName = deviceName;
        _isCapture = isCapture;
        SampleRate = sampleRate;
        Channels = channels;
    }

    public static AlsaPcmDevice Open(
        string deviceName,
        bool capture,
        uint sampleRate = 16000,
        uint channels = 2,
        uint latencyUs = 100_000)
    {
        var stream = capture
            ? AlsaInterop.SND_PCM_STREAM_CAPTURE
            : AlsaInterop.SND_PCM_STREAM_PLAYBACK;

        var err = AlsaInterop.Open(out var pcm, deviceName, stream, 0);
        if (err < 0)
        {
            throw new InvalidOperationException(
                $"Failed to open ALSA device '{deviceName}': {AlsaInterop.StrError(err)}");
        }

        err = AlsaInterop.SetParams(
            pcm,
            AlsaInterop.SND_PCM_FORMAT_S16_LE,
            AlsaInterop.SND_PCM_ACCESS_RW_INTERLEAVED,
            channels,
            sampleRate,
            softResample: 1,
            latencyUs);

        if (err < 0)
        {
            AlsaInterop.Close(pcm);
            throw new InvalidOperationException(
                $"Failed to set params on '{deviceName}': {AlsaInterop.StrError(err)}");
        }

        return new AlsaPcmDevice(pcm, deviceName, capture, sampleRate, channels);
    }

    public byte[] Read(int frameCount)
    {
        ThrowIfDisposed();
        if (!_isCapture)
        {
            throw new InvalidOperationException("Cannot read from a playback device.");
        }
        if (frameCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount), "Frame count cannot be negative.");
        }
        if (frameCount == 0)
        {
            return [];
        }

        var bufferSize = checked(frameCount * BytesPerFrame);
        if (_readScratch.Length < bufferSize)
        {
            _readScratch = GC.AllocateUninitializedArray<byte>(bufferSize);
        }

        var framesRead = ReadFrames(_readScratch, 0, frameCount);
        var actualBytes = checked(framesRead * BytesPerFrame);
        var output = GC.AllocateUninitializedArray<byte>(actualBytes);
        _readScratch.AsSpan(0, actualBytes).CopyTo(output);
        return output;
    }

    public int Write(byte[] pcmBytes, int offset, int frameCount)
    {
        ThrowIfDisposed();
        if (_isCapture)
        {
            throw new InvalidOperationException("Cannot write to a capture device.");
        }
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative.");
        }
        if (frameCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount), "Frame count cannot be negative.");
        }

        var byteCount = checked(frameCount * BytesPerFrame);
        if (offset + byteCount > pcmBytes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount), "Offset + frame count exceeds buffer length.");
        }

        return Write(pcmBytes.AsMemory(offset, byteCount));
    }

    public int Write(ReadOnlyMemory<byte> pcmBytes)
    {
        ThrowIfDisposed();
        if (_isCapture)
        {
            throw new InvalidOperationException("Cannot write to a capture device.");
        }
        if (pcmBytes.Length == 0)
        {
            return 0;
        }
        if (pcmBytes.Length % BytesPerFrame != 0)
        {
            throw new ArgumentException("PCM byte length must be aligned to whole frames.", nameof(pcmBytes));
        }

        var frameCount = pcmBytes.Length / BytesPerFrame;
        if (!MemoryMarshal.TryGetArray(pcmBytes, out var segment) || segment.Array is null)
        {
            // Fallback for non-array-backed memory.
            var copy = pcmBytes.ToArray();
            return Write(copy, 0, frameCount);
        }

        var handle = GCHandle.Alloc(segment.Array, GCHandleType.Pinned);
        try
        {
            var ptr = handle.AddrOfPinnedObject() + segment.Offset;
            var written = AlsaInterop.WriteInterleaved(_pcm, ptr, (nuint)frameCount);

            if (written < 0)
            {
                var recovered = AlsaInterop.Recover(_pcm, (int)written, silent: 1);
                if (recovered < 0)
                {
                    throw new InvalidOperationException(
                        $"ALSA write error on '{_deviceName}' (unrecoverable): {AlsaInterop.StrError((int)written)}");
                }

                written = AlsaInterop.WriteInterleaved(_pcm, ptr, (nuint)frameCount);
                if (written < 0)
                {
                    throw new InvalidOperationException(
                        $"ALSA write failed after recovery: {AlsaInterop.StrError((int)written)}");
                }
            }

            return (int)written;
        }
        finally
        {
            handle.Free();
        }
    }

    private int ReadFrames(byte[] destination, int destinationOffset, int frameCount)
    {
        var handle = GCHandle.Alloc(destination, GCHandleType.Pinned);
        try
        {
            var ptr = handle.AddrOfPinnedObject() + destinationOffset;
            var framesRead = AlsaInterop.ReadInterleaved(_pcm, ptr, (nuint)frameCount);
            if (framesRead < 0)
            {
                var recovered = AlsaInterop.Recover(_pcm, (int)framesRead, silent: 1);
                if (recovered < 0)
                {
                    throw new InvalidOperationException(
                        $"ALSA read error on '{_deviceName}' (unrecoverable): {AlsaInterop.StrError((int)framesRead)}");
                }

                framesRead = AlsaInterop.ReadInterleaved(_pcm, ptr, (nuint)frameCount);
                if (framesRead < 0)
                {
                    throw new InvalidOperationException(
                        $"ALSA read failed after recovery: {AlsaInterop.StrError((int)framesRead)}");
                }
            }

            return (int)framesRead;
        }
        finally
        {
            handle.Free();
        }
    }

    public void Drain()
    {
        ThrowIfDisposed();
        AlsaInterop.Drain(_pcm);
    }

    public void Drop()
    {
        ThrowIfDisposed();
        AlsaInterop.Drop(_pcm);
    }

    public void Prepare()
    {
        ThrowIfDisposed();
        AlsaInterop.Prepare(_pcm);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_pcm != 0)
        {
            AlsaInterop.Drop(_pcm);
            AlsaInterop.Close(_pcm);
            _pcm = 0;
        }

        _readScratch = Array.Empty<byte>();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
