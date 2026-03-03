using System.Threading.Channels;
using System.Buffers.Binary;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReachTether.Audio;
using ReachTether.Audio.Alsa;

internal interface IAudioCapturePipeline
{
    Task<UtteranceCaptureResult> CaptureUtteranceAsync(CancellationToken cancellationToken = default);
}

internal sealed record UtteranceCaptureResult(
    AudioFrame[] Frames,
    bool SpeechDetected,
    string? FailureReason,
    int DurationMs);

internal sealed class AudioCaptureService(
    LocalAudioSession audioSession,
    RobotAppOptions options,
    ILogger<AudioCaptureService> logger) : BackgroundService, IAudioCapturePipeline
{
    private readonly Channel<AudioFrame> _channel = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleWriter = true,
        SingleReader = false
    });

    private readonly SemaphoreSlim _captureLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var frame = await audioSession.CaptureChunkAsync(stoppingToken);
                await _channel.Writer.WriteAsync(frame, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await Task.Delay(100, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Audio capture loop fault; retrying.");

                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await Task.Delay(250, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        _channel.Writer.TryComplete();
    }

    public async Task<UtteranceCaptureResult> CaptureUtteranceAsync(CancellationToken cancellationToken = default)
    {
        await _captureLock.WaitAsync(cancellationToken);
        try
        {
            while (_channel.Reader.TryRead(out _))
            {
            }

            var vad = options.Vad;
            var listenDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(vad.ListenTimeoutMs);

            var preRollFrames = new Queue<AudioFrame>();
            var preRollDurationMs = 0;
            var utteranceFrames = new List<AudioFrame>();

            var speechDetected = false;
            var aboveThresholdFrames = 0;
            var trailingSilenceMs = 0;
            var utteranceDurationMs = 0;
            var noiseFloorRms = vad.InitialNoiseFloorRms;

            while (!cancellationToken.IsCancellationRequested)
            {
                var remaining = speechDetected
                    ? TimeSpan.FromMilliseconds(Math.Max(250, vad.EndSilenceMs))
                    : listenDeadline - DateTime.UtcNow;

                if (remaining <= TimeSpan.Zero)
                {
                    return new UtteranceCaptureResult([], false, "No speech detected before listen timeout.", 0);
                }

                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readCts.CancelAfter(remaining);

                AudioFrame frame;
                try
                {
                    frame = await _channel.Reader.ReadAsync(readCts.Token);
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    if (!speechDetected)
                    {
                        return new UtteranceCaptureResult([], false, "No speech detected before listen timeout.", 0);
                    }

                    break;
                }
                catch (ChannelClosedException)
                {
                    return new UtteranceCaptureResult([], false, "Audio capture channel is closed.", 0);
                }

                var frameDurationMs = GetFrameDurationMs(frame);
                var frameRms = ComputeRms(frame);
                var threshold = Math.Max(vad.MinRms, noiseFloorRms * vad.NoiseMultiplier);
                var isSpeechFrame = frameRms >= threshold;

                if (!speechDetected)
                {
                    preRollFrames.Enqueue(frame);
                    preRollDurationMs += frameDurationMs;

                    while (preRollDurationMs > vad.PreRollMs && preRollFrames.Count > 0)
                    {
                        preRollDurationMs -= GetFrameDurationMs(preRollFrames.Dequeue());
                    }

                    noiseFloorRms = Math.Min(
                        vad.MaxNoiseFloorRms,
                        ((1.0 - vad.NoiseFloorAdaptation) * noiseFloorRms) + (vad.NoiseFloorAdaptation * frameRms));

                    if (isSpeechFrame)
                    {
                        aboveThresholdFrames++;
                    }
                    else
                    {
                        aboveThresholdFrames = 0;
                    }

                    if (aboveThresholdFrames >= vad.StartTriggerFrames)
                    {
                        speechDetected = true;
                        utteranceFrames.AddRange(preRollFrames);
                        utteranceDurationMs = preRollDurationMs;
                    }

                    continue;
                }

                utteranceFrames.Add(frame);
                utteranceDurationMs += frameDurationMs;

                if (isSpeechFrame)
                {
                    trailingSilenceMs = 0;
                }
                else
                {
                    trailingSilenceMs += frameDurationMs;
                    noiseFloorRms = Math.Min(
                        vad.MaxNoiseFloorRms,
                        ((1.0 - vad.NoiseFloorAdaptation) * noiseFloorRms) + (vad.NoiseFloorAdaptation * frameRms));
                }

                if (trailingSilenceMs >= vad.EndSilenceMs)
                {
                    break;
                }

                if (utteranceDurationMs >= vad.MaxUtteranceMs)
                {
                    break;
                }
            }

            if (utteranceFrames.Count == 0)
            {
                return new UtteranceCaptureResult([], false, "No speech detected.", 0);
            }

            return new UtteranceCaptureResult([.. utteranceFrames], true, null, utteranceDurationMs);
        }
        finally
        {
            _captureLock.Release();
        }
    }

    private static int GetFrameDurationMs(AudioFrame frame)
    {
        var blockAlign = frame.Format.BlockAlign;
        var sampleRate = frame.Format.SampleRateHz;
        if (blockAlign <= 0 || sampleRate <= 0 || frame.Pcm16Bytes.Length < blockAlign)
        {
            return 0;
        }

        var frameCount = frame.Pcm16Bytes.Length / blockAlign;
        if (frameCount <= 0)
        {
            return 0;
        }

        return Math.Max(1, (int)Math.Round(frameCount * 1000.0 / sampleRate));
    }

    private static double ComputeRms(AudioFrame frame)
    {
        var pcm = frame.Pcm16Bytes.AsSpan();
        if (pcm.Length < 2)
        {
            return 0;
        }

        var channels = Math.Max(1, (int)frame.Format.Channels);
        var bytesPerSample = 2;
        var stride = channels * bytesPerSample;
        if (pcm.Length < stride)
        {
            return 0;
        }

        long sumSquares = 0;
        var monoSamples = 0;

        for (var offset = 0; offset + stride <= pcm.Length; offset += stride)
        {
            var mixed = 0;

            for (var channel = 0; channel < channels; channel++)
            {
                var sampleOffset = offset + (channel * bytesPerSample);
                var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(sampleOffset, bytesPerSample));
                mixed += sample;
            }

            var mono = mixed / channels;
            sumSquares += (long)mono * mono;
            monoSamples++;
        }

        if (monoSamples == 0)
        {
            return 0;
        }

        var meanSquare = (double)sumSquares / monoSamples;
        return Math.Sqrt(meanSquare) / short.MaxValue;
    }
}
