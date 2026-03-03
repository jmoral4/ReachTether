using System.Text.Json.Nodes;
using ReachTether.Audio;
using ReachTether.WebRtc.Abstractions;
using ReachTether.WebRtc.Models;

namespace ReachTether.Audio.Alsa;

public sealed class LocalAudioSession : IReachySession
{
    private readonly LocalAudioOptions _options;
    private readonly object _captureSync = new();
    private readonly object _playbackSync = new();

    private AlsaPcmDevice? _captureDevice;
    private AlsaPcmDevice? _playbackDevice;
    private PlaybackStreamState? _playbackStream;
    private volatile bool _disposed;
    private int _playbackFlushVersion;

    public ReachySessionState State { get; private set; } = ReachySessionState.Disconnected;
    public string CorrelationId { get; } = Guid.NewGuid().ToString("N");
    public event Action<ReachySessionState>? StateChanged;

    public LocalAudioSession(LocalAudioOptions? options = null)
    {
        _options = options ?? new LocalAudioOptions();
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        lock (_captureSync)
        {
            _captureDevice ??= AlsaPcmDevice.Open(
                _options.CaptureDevice,
                capture: true,
                _options.SampleRate,
                _options.Channels,
                _options.LatencyUs);
        }

        lock (_playbackSync)
        {
            _playbackDevice ??= AlsaPcmDevice.Open(
                _options.PlaybackDevice,
                capture: false,
                _options.SampleRate,
                _options.Channels,
                _options.LatencyUs);
        }

        SetState(ReachySessionState.Streaming);
        Console.WriteLine(
            $"[LocalAudio] ALSA devices connected: capture='{_options.CaptureDevice}', playback='{_options.PlaybackDevice}', rate={_options.SampleRate}, ch={_options.Channels}");
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_captureSync)
        {
            _captureDevice?.Dispose();
            _captureDevice = null;
        }

        lock (_playbackSync)
        {
            _playbackDevice?.Dispose();
            _playbackDevice = null;
        }

        SetState(ReachySessionState.Disconnected);
        return Task.CompletedTask;
    }

    public Task<AudioFrame> CaptureChunkAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        lock (_captureSync)
        {
            if (State != ReachySessionState.Streaming || _captureDevice is null)
            {
                throw new InvalidOperationException("LocalAudioSession is not connected. Call ConnectAsync before streaming audio.");
            }

            var chunkFrames = Math.Max(1, (int)(_options.SampleRate * _options.ReadChunkMs / 1000));
            var format = new AudioFormat((int)_options.SampleRate, (short)_options.Channels, 16);
            var pcm = _captureDevice.Read(chunkFrames);
            return Task.FromResult(new AudioFrame(
                pcm,
                format,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }
    }

    public async Task<AudioFrame[]> CaptureFramesAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            return [];
        }

        EnsureConnected();

        var totalFrames = (int)(duration.TotalSeconds * _options.SampleRate);
        var chunkFrames = Math.Max(1, (int)(_options.SampleRate * _options.ReadChunkMs / 1000));
        var format = new AudioFormat((int)_options.SampleRate, (short)_options.Channels, 16);

        var frames = new List<AudioFrame>();
        var framesRemaining = totalFrames;

        while (framesRemaining > 0 && !cancellationToken.IsCancellationRequested)
        {
            var toRead = Math.Min(chunkFrames, framesRemaining);
            byte[] pcm;

            lock (_captureSync)
            {
                if (_captureDevice is null)
                {
                    throw new InvalidOperationException("LocalAudioSession is not connected. Call ConnectAsync before streaming audio.");
                }

                pcm = _captureDevice.Read(toRead);
            }

            if (pcm.Length > 0)
            {
                frames.Add(new AudioFrame(
                    pcm,
                    format,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            }

            framesRemaining -= toRead;
            await Task.Yield();
        }

        return [.. frames];
    }

    public async Task PlayWaveAsync(byte[] wavBytes, CancellationToken cancellationToken = default)
    {
        EnsureConnected();

        var (pcm, wavFormat) = WavePcm16.Decode(wavBytes);
        if (wavFormat.Channels <= 0)
        {
            throw new InvalidDataException($"Invalid WAV channel count: {wavFormat.Channels}.");
        }
        if (wavFormat.SampleRateHz <= 0)
        {
            throw new InvalidDataException($"Invalid WAV sample rate: {wavFormat.SampleRateHz}.");
        }

        var sourceRate = wavFormat.SampleRateHz;
        var targetRate = (int)_options.SampleRate;
        pcm = AdjustChannels(pcm, wavFormat.Channels, (short)_options.Channels);
        pcm = ResamplePcm16(pcm, sourceRate, targetRate, _options.Channels);

        var bytesPerFrame = (int)_options.Channels * 2;
        var chunkFrames = Math.Max(1, (int)(_options.SampleRate * _options.WriteChunkMs / 1000));
        var offset = 0;
        var flushVersionAtStart = Volatile.Read(ref _playbackFlushVersion);

        try
        {
            while (offset < pcm.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (flushVersionAtStart != Volatile.Read(ref _playbackFlushVersion))
                {
                    break;
                }

                var framesToWrite = Math.Min(chunkFrames, (pcm.Length - offset) / bytesPerFrame);
                int written;

                lock (_playbackSync)
                {
                    written = _playbackDevice!.Write(pcm, offset, framesToWrite);
                }

                if (written <= 0)
                {
                    break;
                }

                offset += written * bytesPerFrame;
                await Task.Yield();
            }
        }
        catch (OperationCanceledException)
        {
            lock (_playbackSync)
            {
                _playbackDevice!.Drop();
                _playbackDevice.Prepare();
            }

            throw;
        }

        lock (_playbackSync)
        {
            if (cancellationToken.IsCancellationRequested || flushVersionAtStart != Volatile.Read(ref _playbackFlushVersion))
            {
                _playbackDevice!.Drop();
                _playbackDevice.Prepare();
                return;
            }

            _playbackDevice!.Drain();
        }
    }

    public void BeginPlaybackStream(AudioFormat sourceFormat)
    {
        EnsureConnected();

        if (sourceFormat.BitsPerSample != 16)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceFormat), "Only PCM16 streams are supported.");
        }
        if (sourceFormat.Channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceFormat), "Channel count must be greater than zero.");
        }
        if (sourceFormat.SampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceFormat), "Sample rate must be greater than zero.");
        }

        lock (_playbackSync)
        {
            _playbackDevice!.Drop();
            _playbackDevice.Prepare();
            _playbackStream = new PlaybackStreamState(
                sourceFormat,
                Volatile.Read(ref _playbackFlushVersion));
        }
    }

    public void WritePlaybackPcm16Chunk(byte[] pcmChunk, CancellationToken cancellationToken = default)
    {
        if (pcmChunk.Length == 0)
        {
            return;
        }

        EnsureConnected();
        cancellationToken.ThrowIfCancellationRequested();

        PlaybackStreamState streamState;
        lock (_playbackSync)
        {
            streamState = _playbackStream
                ?? throw new InvalidOperationException("Playback stream is not active. Call BeginPlaybackStream first.");
        }

        if (streamState.FlushVersionAtStart != Volatile.Read(ref _playbackFlushVersion))
        {
            return;
        }

        var converted = AdjustChannels(pcmChunk, streamState.SourceFormat.Channels, (short)_options.Channels);
        converted = ResamplePcm16(
            converted,
            streamState.SourceFormat.SampleRateHz,
            (int)_options.SampleRate,
            _options.Channels);

        var bytesPerFrame = (int)_options.Channels * 2;
        var offset = 0;

        while (offset < converted.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (streamState.FlushVersionAtStart != Volatile.Read(ref _playbackFlushVersion))
            {
                return;
            }

            var framesToWrite = (converted.Length - offset) / bytesPerFrame;
            if (framesToWrite <= 0)
            {
                break;
            }

            int written;
            lock (_playbackSync)
            {
                written = _playbackDevice!.Write(converted, offset, framesToWrite);
            }

            if (written <= 0)
            {
                break;
            }

            offset += written * bytesPerFrame;
        }
    }

    public void CompletePlaybackStream()
    {
        EnsureConnected();

        lock (_playbackSync)
        {
            if (_playbackStream is null)
            {
                return;
            }

            if (_playbackStream.FlushVersionAtStart != Volatile.Read(ref _playbackFlushVersion))
            {
                _playbackDevice!.Drop();
                _playbackDevice.Prepare();
                _playbackStream = null;
                return;
            }

            _playbackDevice!.Drain();
            _playbackStream = null;
        }
    }

    public void CancelPlaybackStream()
    {
        lock (_playbackSync)
        {
            if (_playbackStream is null || _playbackDevice is null)
            {
                _playbackStream = null;
                return;
            }

            _playbackDevice.Drop();
            _playbackDevice.Prepare();
            _playbackStream = null;
        }
    }

    public void FlushPlayback()
    {
        Interlocked.Increment(ref _playbackFlushVersion);

        lock (_playbackSync)
        {
            _playbackStream = null;

            if (_playbackDevice is null)
            {
                return;
            }

            _playbackDevice.Drop();
            _playbackDevice.Prepare();
        }
    }

    public Task SendCommandAsync(JsonObject command, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync();
    }

    private void EnsureConnected()
    {
        ThrowIfDisposed();

        if (State != ReachySessionState.Streaming || _captureDevice is null || _playbackDevice is null)
        {
            throw new InvalidOperationException("LocalAudioSession is not connected. Call ConnectAsync before streaming audio.");
        }
    }

    private void SetState(ReachySessionState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(state);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static byte[] AdjustChannels(byte[] pcm, short sourceChannels, short targetChannels)
    {
        if (sourceChannels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceChannels), "Source channel count must be greater than zero.");
        }
        if (targetChannels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetChannels), "Target channel count must be greater than zero.");
        }
        if (sourceChannels == targetChannels)
        {
            return pcm;
        }

        var samples = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm, 0, samples, 0, pcm.Length);

        short[] result;
        if (sourceChannels == 1 && targetChannels == 2)
        {
            result = new short[samples.Length * 2];
            for (var i = 0; i < samples.Length; i++)
            {
                result[i * 2] = samples[i];
                result[i * 2 + 1] = samples[i];
            }
        }
        else if (sourceChannels == 2 && targetChannels == 1)
        {
            result = new short[samples.Length / 2];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = (short)((samples[i * 2] + samples[i * 2 + 1]) / 2);
            }
        }
        else
        {
            var sourceFrames = samples.Length / sourceChannels;
            result = new short[sourceFrames * targetChannels];
            for (var frame = 0; frame < sourceFrames; frame++)
            {
                var sample = samples[frame * sourceChannels];
                for (var channel = 0; channel < targetChannels; channel++)
                {
                    result[frame * targetChannels + channel] = sample;
                }
            }
        }

        var output = new byte[result.Length * 2];
        Buffer.BlockCopy(result, 0, output, 0, output.Length);
        return output;
    }

    private static byte[] ResamplePcm16(byte[] pcm, int sourceRate, int targetRate, uint channels)
    {
        if (sourceRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceRate), "Source sample rate must be greater than zero.");
        }
        if (targetRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetRate), "Target sample rate must be greater than zero.");
        }
        if (channels == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), "Channel count must be greater than zero.");
        }
        if (sourceRate == targetRate || pcm.Length == 0)
        {
            return pcm;
        }

        var ch = (int)channels;
        var sourceSamples = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm, 0, sourceSamples, 0, pcm.Length);

        var sourceFrames = sourceSamples.Length / ch;
        if (sourceFrames <= 1)
        {
            return pcm;
        }

        var targetFrames = Math.Max(1, (int)Math.Round(sourceFrames * (double)targetRate / sourceRate));
        var resampled = new short[targetFrames * ch];

        for (var frame = 0; frame < targetFrames; frame++)
        {
            var sourcePosition = frame * (double)sourceRate / targetRate;
            var sourceIndex = (int)Math.Floor(sourcePosition);
            var nextIndex = Math.Min(sourceIndex + 1, sourceFrames - 1);
            var fraction = sourcePosition - sourceIndex;

            for (var channel = 0; channel < ch; channel++)
            {
                var a = sourceSamples[sourceIndex * ch + channel];
                var b = sourceSamples[nextIndex * ch + channel];
                var mixed = a + (b - a) * fraction;
                resampled[frame * ch + channel] = (short)Math.Clamp((int)Math.Round(mixed), short.MinValue, short.MaxValue);
            }
        }

        var output = new byte[resampled.Length * 2];
        Buffer.BlockCopy(resampled, 0, output, 0, output.Length);
        return output;
    }

    private sealed record PlaybackStreamState(
        AudioFormat SourceFormat,
        int FlushVersionAtStart);
}
