internal sealed class HeadTrackingController
{
    private readonly RobotAppOptions.VisionSettings _settings;
    private readonly ILookAtProjector _projector;
    private readonly object _sync = new();
    private TrackingObservation? _latestObservation;
    private HeadPose _smoothedPose = HeadPose.Neutral;
    private HeadPose _returnStartPose = HeadPose.Neutral;
    private DateTimeOffset? _lastDetectionAtUtc;
    private DateTimeOffset? _lastPoseUpdateAtUtc;
    private DateTimeOffset? _returnStartAtUtc;
    private int _consecutiveHits;
    private bool _targetLocked;

    public HeadTrackingController(RobotAppOptions options, ILookAtProjector projector)
    {
        _settings = options.Vision;
        _projector = projector;
    }

    public void UpdateObservation(TrackingObservation? observation)
    {
        lock (_sync)
        {
            if (observation is null || observation.Confidence < _settings.FaceTrackingMinimumConfidence)
            {
                _latestObservation = null;
                _consecutiveHits = 0;
                return;
            }

            _latestObservation = observation;
            _lastDetectionAtUtc = observation.TimestampUtc;
            _returnStartAtUtc = null;
            _consecutiveHits = Math.Min(_consecutiveHits + 1, _settings.FaceTrackingLockOnConsecutiveHits);
            if (_consecutiveHits >= _settings.FaceTrackingLockOnConsecutiveHits)
            {
                _targetLocked = true;
            }
        }
    }

    public TrackingCommand GetTrackingCommand(DateTimeOffset nowUtc, CameraCalibration calibration)
    {
        lock (_sync)
        {
            var deltaSeconds = GetDeltaSeconds(nowUtc);
            HeadPose targetPose;
            var confidence = 0.0;

            if (_targetLocked && _latestObservation is not null && _lastDetectionAtUtc is not null)
            {
                var targetAge = nowUtc - _lastDetectionAtUtc.Value;
                if (targetAge <= TimeSpan.FromSeconds(_settings.FaceTrackingHoldSeconds))
                {
                    targetPose = _projector.Project(_latestObservation.CenterX, _latestObservation.CenterY, calibration);
                    confidence = _latestObservation.Confidence;
                    _returnStartAtUtc = null;
                }
                else
                {
                    targetPose = GetReturnToNeutralTarget(nowUtc);
                }
            }
            else
            {
                targetPose = HeadPose.Neutral;
            }

            targetPose = ApplyDeadband(targetPose);
            _smoothedPose = SmoothAndRateLimit(_smoothedPose, targetPose, deltaSeconds);
            if (IsNearNeutral(_smoothedPose) && IsNearNeutral(targetPose))
            {
                _smoothedPose = HeadPose.Neutral;
                _targetLocked = false;
            }

            return new TrackingCommand(_smoothedPose, confidence, nowUtc);
        }
    }

    private HeadPose GetReturnToNeutralTarget(DateTimeOffset nowUtc)
    {
        if (_returnStartAtUtc is null)
        {
            _returnStartAtUtc = nowUtc;
            _returnStartPose = _smoothedPose;
        }

        var returnDuration = TimeSpan.FromSeconds(_settings.FaceTrackingReturnToNeutralSeconds);
        if (returnDuration <= TimeSpan.Zero)
        {
            return HeadPose.Neutral;
        }

        var blend = Math.Clamp((nowUtc - _returnStartAtUtc.Value).TotalSeconds / returnDuration.TotalSeconds, 0.0, 1.0);
        if (blend >= 1.0)
        {
            return HeadPose.Neutral;
        }

        return new HeadPose(
            Lerp(_returnStartPose.YawRadians, 0.0, blend),
            Lerp(_returnStartPose.PitchRadians, 0.0, blend),
            Lerp(_returnStartPose.RollRadians, 0.0, blend));
    }

    private HeadPose ApplyDeadband(HeadPose pose)
    {
        var deadbandRadians = DegreesToRadians(_settings.FaceTrackingDeadbandDegrees);
        var yaw = Math.Abs(pose.YawRadians) < deadbandRadians ? 0.0 : pose.YawRadians;
        var pitch = Math.Abs(pose.PitchRadians) < deadbandRadians ? 0.0 : pose.PitchRadians;
        return new HeadPose(yaw, pitch, pose.RollRadians);
    }

    private HeadPose SmoothAndRateLimit(HeadPose current, HeadPose target, double deltaSeconds)
    {
        var alpha = Math.Clamp(_settings.FaceTrackingSmoothing, 0.0, 1.0);
        var smoothed = new HeadPose(
            Lerp(current.YawRadians, target.YawRadians, alpha),
            Lerp(current.PitchRadians, target.PitchRadians, alpha),
            Lerp(current.RollRadians, target.RollRadians, alpha));

        var maxStep = DegreesToRadians(_settings.FaceTrackingMaxAngularVelocityDegPerSecond) * Math.Max(0.0, deltaSeconds);
        return new HeadPose(
            ClampDelta(current.YawRadians, smoothed.YawRadians, maxStep),
            ClampDelta(current.PitchRadians, smoothed.PitchRadians, maxStep),
            ClampDelta(current.RollRadians, smoothed.RollRadians, maxStep));
    }

    private double GetDeltaSeconds(DateTimeOffset nowUtc)
    {
        var deltaSeconds = _lastPoseUpdateAtUtc is null
            ? 1.0 / Math.Max(1, _settings.FaceTrackingControlHz)
            : Math.Max(0.0, (nowUtc - _lastPoseUpdateAtUtc.Value).TotalSeconds);
        _lastPoseUpdateAtUtc = nowUtc;
        return deltaSeconds;
    }

    private static double ClampDelta(double current, double target, double maxStep)
    {
        if (maxStep <= 0.0)
        {
            return target;
        }

        var delta = target - current;
        if (Math.Abs(delta) <= maxStep)
        {
            return target;
        }

        return current + (Math.Sign(delta) * maxStep);
    }

    private static double DegreesToRadians(double degrees)
    {
        return degrees * Math.PI / 180.0;
    }

    private static double Lerp(double from, double to, double amount)
    {
        return from + ((to - from) * Math.Clamp(amount, 0.0, 1.0));
    }

    private static bool IsNearNeutral(HeadPose pose)
    {
        const double epsilon = 0.0005;
        return Math.Abs(pose.YawRadians) <= epsilon
            && Math.Abs(pose.PitchRadians) <= epsilon
            && Math.Abs(pose.RollRadians) <= epsilon;
    }
}
