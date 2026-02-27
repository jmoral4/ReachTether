using System.Text.Json.Nodes;
using ReachTether.Audio;
using ReachTether.WebRtc.Abstractions;
using ReachTether.WebRtc.Models;

namespace ReachTether.Audio.Alsa;

public sealed class LocalAudioSession : IReachySession
{
    private readonly LocalAudioOptions _options;

    public ReachySessionState State { get; private set; } = ReachySessionState.Disconnected;
    public string CorrelationId { get; } = Guid.NewGuid().ToString("N");
    public event Action<ReachySessionState>? StateChanged;

    public LocalAudioSession(LocalAudioOptions? options = null)
    {
        _options = options ?? new LocalAudioOptions();
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        using (AlsaPcmDevice.Open(_options.CaptureDevice, capture: true, _options.SampleRate, _options.Channels))
        {
        }

        using (AlsaPcmDevice.Open(_options.PlaybackDevice, capture: false, _options.SampleRate, _options.Channels))
        {
        }

        SetState(ReachySessionState.Streaming);
        Console.WriteLine(
            $"[LocalAudio] ALSA devices verified: capture='{_options.CaptureDevice}', playback='{_options.PlaybackDevice}', rate={_options.SampleRate}, ch={_options.Channels}");
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(ReachySessionState.Disconnected);
        return Task.CompletedTask;
    }

    public async Task<AudioFrame[]> CaptureFramesAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            return [];
        }

        var totalFrames = (int)(duration.TotalSeconds * _options.SampleRate);
        var chunkFrames = Math.Max(1, (int)(_options.SampleRate * _options.ReadChunkMs / 1000));
        var format = new AudioFormat((int)_options.SampleRate, (short)_options.Channels, 16);

        using var device = AlsaPcmDevice.Open(
            _options.CaptureDevice,
            capture: true,
            _options.SampleRate,
            _options.Channels,
            _options.LatencyUs);

        var frames = new List<AudioFrame>();
        var framesRemaining = totalFrames;

        while (framesRemaining > 0 && !cancellationToken.IsCancellationRequested)
        {
            var toRead = Math.Min(chunkFrames, framesRemaining);
            var pcm = device.Read(toRead);

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

        using var device = AlsaPcmDevice.Open(
            _options.PlaybackDevice,
            capture: false,
            _options.SampleRate,
            _options.Channels,
            _options.LatencyUs);

        var bytesPerFrame = (int)_options.Channels * 2;
        var chunkFrames = Math.Max(1, (int)(_options.SampleRate * _options.WriteChunkMs / 1000));
        var offset = 0;

        while (offset < pcm.Length && !cancellationToken.IsCancellationRequested)
        {
            var framesToWrite = Math.Min(chunkFrames, (pcm.Length - offset) / bytesPerFrame);
            var written = device.Write(pcm, offset, framesToWrite);
            if (written <= 0)
            {
                break;
            }

            offset += written * bytesPerFrame;
            await Task.Yield();
        }

        device.Drain();
    }

    public Task SendCommandAsync(JsonObject command, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        SetState(ReachySessionState.Disconnected);
        return ValueTask.CompletedTask;
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
}
