using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReachyMini.Sdk;
using ReachyMini.Sdk.Models;

internal interface IMotionOrchestrator
{
    void PushAssistantAudioPcm16(byte[] pcm16Bytes, int sampleRateHz, short channels = 1);
    void ResetTalkingGesture();
    void SetRobotMotionEnabled(bool enabled);
}

internal sealed class MotionOrchestrator(
    ReachyMiniClient reachyClient,
    IInteractionStateMachine stateMachine,
    RobotAppOptions options,
    ILogger<MotionOrchestrator> logger) : BackgroundService, IMotionOrchestrator
{
    private readonly RobotAppOptions.MotionSettings _settings = options.Motion;
    private readonly TalkingGestureSource _talkingGestureSource = new(options.Motion);

    private MotionOffsets _lastSentOffsets = MotionOffsets.Zero;
    private DateTime _lastSetTargetErrorLogUtc = DateTime.MinValue;
    private int _suppressedSetTargetErrors;
    private volatile bool _robotMotionEnabled;

    public void PushAssistantAudioPcm16(byte[] pcm16Bytes, int sampleRateHz, short channels = 1)
    {
        if (!_settings.Enabled || pcm16Bytes.Length == 0)
        {
            return;
        }

        _talkingGestureSource.FeedPcm16(pcm16Bytes, sampleRateHz, channels);
    }

    public void ResetTalkingGesture()
    {
        _talkingGestureSource.Reset();
    }

    public void SetRobotMotionEnabled(bool enabled)
    {
        _robotMotionEnabled = enabled;
        if (!enabled)
        {
            _talkingGestureSource.Reset();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            logger.LogInformation("Motion orchestrator is disabled by configuration.");
            return;
        }

        var loopHz = Math.Clamp(_settings.LoopHz, 10, 100);
        var loopInterval = TimeSpan.FromSeconds(1.0 / loopHz);
        var metricsIntervalSeconds = Math.Max(1, _settings.MetricsIntervalSeconds);

        logger.LogInformation("Motion orchestrator started at target {LoopHz} Hz.", loopHz);

        var clock = Stopwatch.StartNew();
        var lastTick = clock.Elapsed;
        var nextTick = lastTick;
        var metricsWindowStart = lastTick;
        var tickCount = 0;
        var wasDisabled = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_robotMotionEnabled)
            {
                if (!wasDisabled)
                {
                    wasDisabled = true;
                    _lastSentOffsets = MotionOffsets.Zero;
                    _talkingGestureSource.Reset();
                }

                await Task.Delay(loopInterval, stoppingToken);
                continue;
            }

            wasDisabled = false;
            var tickStart = clock.Elapsed;
            var deltaSeconds = Math.Max(0, (tickStart - lastTick).TotalSeconds);
            lastTick = tickStart;

            var speaking = stateMachine.Current == InteractionState.Speaking;
            var sampledOffsets = _talkingGestureSource.Sample(deltaSeconds, speaking);
            var clampedOffsets = ClampOffsets(sampledOffsets);
            var shouldSend = speaking || !clampedOffsets.IsNearZero() || !_lastSentOffsets.IsNearZero();

            if (shouldSend && HasMeaningfulDelta(clampedOffsets, _lastSentOffsets))
            {
                var sent = await SendTargetAsync(clampedOffsets, stoppingToken);
                if (sent)
                {
                    _lastSentOffsets = clampedOffsets;
                }
            }

            tickCount++;
            var metricsElapsedSeconds = (tickStart - metricsWindowStart).TotalSeconds;
            if (metricsElapsedSeconds >= metricsIntervalSeconds)
            {
                var actualHz = tickCount / metricsElapsedSeconds;
                logger.LogInformation(
                    "Motion loop stats: actualHz={ActualHz:F1}, speaking={Speaking}, queuedAudio={QueuedAudio}",
                    actualHz,
                    speaking,
                    !clampedOffsets.IsNearZero());
                tickCount = 0;
                metricsWindowStart = tickStart;
            }

            nextTick += loopInterval;
            var remaining = nextTick - clock.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, stoppingToken);
            }
            else if (remaining < -loopInterval)
            {
                // If we fall more than one tick behind, realign to avoid unbounded drift.
                nextTick = clock.Elapsed;
            }
        }
    }

    private async Task<bool> SendTargetAsync(MotionOffsets offsets, CancellationToken cancellationToken)
    {
        var request = new FullBodyTarget
        {
            TargetHeadPose = new XYZRPYPose
            {
                X = offsets.XMeters,
                Y = offsets.YMeters,
                Z = offsets.ZMeters,
                Roll = offsets.RollRadians,
                Pitch = offsets.PitchRadians,
                Yaw = offsets.YawRadians
            },
            Timestamp = DateTime.UtcNow
        };

        try
        {
            await reachyClient.Move.SetTargetAsync(request, cancellationToken);
            if (_suppressedSetTargetErrors > 0)
            {
                logger.LogInformation("Motion loop recovered after {Suppressed} suppressed set_target errors.", _suppressedSetTargetErrors);
                _suppressedSetTargetErrors = 0;
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastSetTargetErrorLogUtc).TotalSeconds >= 1)
            {
                var message = _suppressedSetTargetErrors > 0
                    ? $"set_target failed (suppressed {_suppressedSetTargetErrors} repeats)."
                    : "set_target failed.";
                logger.LogWarning(ex, message);
                _suppressedSetTargetErrors = 0;
                _lastSetTargetErrorLogUtc = now;
            }
            else
            {
                _suppressedSetTargetErrors++;
            }

            return false;
        }
    }

    private MotionOffsets ClampOffsets(MotionOffsets offsets)
    {
        var maxTranslationMeters = _settings.MaxTranslationMm / 1000.0;
        var maxRotationRadians = DegToRad(_settings.MaxRotationDeg);

        return new MotionOffsets(
            XMeters: Math.Clamp(offsets.XMeters, -maxTranslationMeters, maxTranslationMeters),
            YMeters: Math.Clamp(offsets.YMeters, -maxTranslationMeters, maxTranslationMeters),
            ZMeters: Math.Clamp(offsets.ZMeters, -maxTranslationMeters, maxTranslationMeters),
            RollRadians: Math.Clamp(offsets.RollRadians, -maxRotationRadians, maxRotationRadians),
            PitchRadians: Math.Clamp(offsets.PitchRadians, -maxRotationRadians, maxRotationRadians),
            YawRadians: Math.Clamp(offsets.YawRadians, -maxRotationRadians, maxRotationRadians));
    }

    private bool HasMeaningfulDelta(MotionOffsets current, MotionOffsets previous)
    {
        var translationThresholdMeters = _settings.CommandThresholdMm / 1000.0;
        var rotationThresholdRadians = DegToRad(_settings.CommandThresholdDeg);

        return Math.Abs(current.XMeters - previous.XMeters) >= translationThresholdMeters
            || Math.Abs(current.YMeters - previous.YMeters) >= translationThresholdMeters
            || Math.Abs(current.ZMeters - previous.ZMeters) >= translationThresholdMeters
            || Math.Abs(current.RollRadians - previous.RollRadians) >= rotationThresholdRadians
            || Math.Abs(current.PitchRadians - previous.PitchRadians) >= rotationThresholdRadians
            || Math.Abs(current.YawRadians - previous.YawRadians) >= rotationThresholdRadians;
    }

    private static double DegToRad(double degrees)
    {
        return degrees * Math.PI / 180.0;
    }
}
