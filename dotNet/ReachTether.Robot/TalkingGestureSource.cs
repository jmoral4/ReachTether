internal sealed class TalkingGestureSource
{
    private static readonly TimeSpan HopDuration = TimeSpan.FromMilliseconds(SwayRollRt.HopMs);

    private readonly object _gate = new();
    private readonly RobotAppOptions.MotionSettings _settings;
    private readonly SwayRollRt _sway = new();
    private readonly Queue<MotionOffsets> _pendingOffsets = new();

    private MotionOffsets _currentOffsets = MotionOffsets.Zero;
    private DateTime _lastAudioFeedUtc = DateTime.MinValue;
    private double _hopAccumulatorSeconds;

    public TalkingGestureSource(RobotAppOptions.MotionSettings settings)
    {
        _settings = settings;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _pendingOffsets.Clear();
            _currentOffsets = MotionOffsets.Zero;
            _lastAudioFeedUtc = DateTime.MinValue;
            _hopAccumulatorSeconds = 0;
            _sway.Reset();
        }
    }

    public void FeedPcm16(byte[] pcm16Bytes, int sampleRateHz, short channels = 1)
    {
        if (pcm16Bytes.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            var offsets = _sway.FeedPcm16(pcm16Bytes, sampleRateHz, channels);
            _lastAudioFeedUtc = DateTime.UtcNow;
            for (var i = 0; i < offsets.Count; i++)
            {
                _pendingOffsets.Enqueue(offsets[i]);
            }
        }
    }

    public MotionOffsets Sample(double deltaSeconds, bool speaking)
    {
        lock (_gate)
        {
            var dt = Math.Max(0, deltaSeconds);

            if (!speaking)
            {
                _pendingOffsets.Clear();
                _hopAccumulatorSeconds = 0;
                _currentOffsets = DecayToNeutral(_currentOffsets, dt);
                return _currentOffsets;
            }

            _hopAccumulatorSeconds += dt;
            var hopSeconds = HopDuration.TotalSeconds;

            while (_hopAccumulatorSeconds >= hopSeconds)
            {
                _hopAccumulatorSeconds -= hopSeconds;

                if (_pendingOffsets.TryDequeue(out var next))
                {
                    _currentOffsets = next;
                    continue;
                }

                var staleMs = (DateTime.UtcNow - _lastAudioFeedUtc).TotalMilliseconds;
                if (staleMs >= _settings.TalkingSilenceReleaseMs)
                {
                    _currentOffsets = DecayToNeutral(_currentOffsets, hopSeconds);
                }
            }

            return _currentOffsets;
        }
    }

    private MotionOffsets DecayToNeutral(MotionOffsets offsets, double elapsedSeconds)
    {
        if (offsets.IsNearZero())
        {
            return MotionOffsets.Zero;
        }

        var decaySeconds = Math.Max(0.01, _settings.TalkingDecaySeconds);
        var blend = Math.Clamp(elapsedSeconds / decaySeconds, 0.0, 1.0);
        var decayed = MotionOffsets.Lerp(offsets, MotionOffsets.Zero, blend);
        return decayed.IsNearZero() ? MotionOffsets.Zero : decayed;
    }
}
