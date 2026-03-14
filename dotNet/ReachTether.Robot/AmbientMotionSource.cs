internal sealed class AmbientMotionSource
{
    private const double BlendInPerSecond = 2.5;
    private const double BlendOutPerSecond = 4.0;

    private double _timeSeconds;
    private double _blend;

    public void Reset()
    {
        _timeSeconds = 0.0;
        _blend = 0.0;
    }

    public MotionOffsets Sample(double deltaSeconds, InteractionState state)
    {
        var dt = Math.Max(0.0, deltaSeconds);
        _timeSeconds += dt;

        var targetBlend = state is InteractionState.Listening or InteractionState.Thinking ? 1.0 : 0.0;
        var blendRate = targetBlend > _blend ? BlendInPerSecond : BlendOutPerSecond;
        _blend = MoveTowards(_blend, targetBlend, blendRate * dt);
        if (_blend <= 0.0)
        {
            return MotionOffsets.Zero;
        }

        var profile = state == InteractionState.Thinking
            ? AmbientProfile.Thinking
            : AmbientProfile.Listening;

        var phase = _timeSeconds;
        var pitchDegrees = profile.PitchAmplitudeDeg * Math.Sin((phase * profile.PitchFrequencyHz * Math.PI * 2.0) + profile.PitchPhaseOffset);
        var yawDegrees = profile.YawAmplitudeDeg * Math.Sin((phase * profile.YawFrequencyHz * Math.PI * 2.0) + profile.YawPhaseOffset);
        var rollDegrees = profile.RollAmplitudeDeg * Math.Sin(phase * profile.RollFrequencyHz * Math.PI * 2.0);
        var xMeters = profile.XAmplitudeMeters * Math.Sin(phase * profile.XFrequencyHz * Math.PI * 2.0);
        var zMeters = profile.ZAmplitudeMeters * Math.Sin((phase * profile.ZFrequencyHz * Math.PI * 2.0) + profile.ZPhaseOffset);

        return Scale(
            new MotionOffsets(
                XMeters: xMeters,
                YMeters: 0.0,
                ZMeters: zMeters,
                RollRadians: DegreesToRadians(rollDegrees),
                PitchRadians: DegreesToRadians(pitchDegrees),
                YawRadians: DegreesToRadians(yawDegrees)),
            _blend);
    }

    private static MotionOffsets Scale(MotionOffsets offsets, double factor)
    {
        return new MotionOffsets(
            offsets.XMeters * factor,
            offsets.YMeters * factor,
            offsets.ZMeters * factor,
            offsets.RollRadians * factor,
            offsets.PitchRadians * factor,
            offsets.YawRadians * factor);
    }

    private static double MoveTowards(double current, double target, double maxDelta)
    {
        if (Math.Abs(target - current) <= maxDelta)
        {
            return target;
        }

        return current + (Math.Sign(target - current) * maxDelta);
    }

    private static double DegreesToRadians(double degrees)
    {
        return degrees * Math.PI / 180.0;
    }

    private readonly record struct AmbientProfile(
        double PitchAmplitudeDeg,
        double PitchFrequencyHz,
        double PitchPhaseOffset,
        double YawAmplitudeDeg,
        double YawFrequencyHz,
        double YawPhaseOffset,
        double RollAmplitudeDeg,
        double RollFrequencyHz,
        double XAmplitudeMeters,
        double XFrequencyHz,
        double ZAmplitudeMeters,
        double ZFrequencyHz,
        double ZPhaseOffset)
    {
        public static readonly AmbientProfile Listening = new(
            PitchAmplitudeDeg: 1.2,
            PitchFrequencyHz: 0.10,
            PitchPhaseOffset: 0.0,
            YawAmplitudeDeg: 1.6,
            YawFrequencyHz: 0.07,
            YawPhaseOffset: Math.PI / 3.0,
            RollAmplitudeDeg: 0.7,
            RollFrequencyHz: 0.06,
            XAmplitudeMeters: 0.0015,
            XFrequencyHz: 0.08,
            ZAmplitudeMeters: 0.0012,
            ZFrequencyHz: 0.12,
            ZPhaseOffset: Math.PI / 5.0);

        public static readonly AmbientProfile Thinking = new(
            PitchAmplitudeDeg: 2.0,
            PitchFrequencyHz: 0.16,
            PitchPhaseOffset: Math.PI / 6.0,
            YawAmplitudeDeg: 2.4,
            YawFrequencyHz: 0.11,
            YawPhaseOffset: Math.PI / 2.0,
            RollAmplitudeDeg: 1.1,
            RollFrequencyHz: 0.08,
            XAmplitudeMeters: 0.0022,
            XFrequencyHz: 0.12,
            ZAmplitudeMeters: 0.0018,
            ZFrequencyHz: 0.18,
            ZPhaseOffset: Math.PI / 4.0);
    }
}
