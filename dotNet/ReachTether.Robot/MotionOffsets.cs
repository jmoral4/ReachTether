internal readonly record struct MotionOffsets(
    double XMeters,
    double YMeters,
    double ZMeters,
    double RollRadians,
    double PitchRadians,
    double YawRadians)
{
    public static readonly MotionOffsets Zero = new(0, 0, 0, 0, 0, 0);

    public bool IsNearZero(double translationEpsilonMeters = 0.0001, double rotationEpsilonRadians = 0.001)
    {
        return Math.Abs(XMeters) <= translationEpsilonMeters
            && Math.Abs(YMeters) <= translationEpsilonMeters
            && Math.Abs(ZMeters) <= translationEpsilonMeters
            && Math.Abs(RollRadians) <= rotationEpsilonRadians
            && Math.Abs(PitchRadians) <= rotationEpsilonRadians
            && Math.Abs(YawRadians) <= rotationEpsilonRadians;
    }

    public static MotionOffsets Lerp(MotionOffsets from, MotionOffsets to, double amount)
    {
        var t = Math.Clamp(amount, 0.0, 1.0);
        return new MotionOffsets(
            Lerp(from.XMeters, to.XMeters, t),
            Lerp(from.YMeters, to.YMeters, t),
            Lerp(from.ZMeters, to.ZMeters, t),
            Lerp(from.RollRadians, to.RollRadians, t),
            Lerp(from.PitchRadians, to.PitchRadians, t),
            Lerp(from.YawRadians, to.YawRadians, t));
    }

    public static MotionOffsets Add(MotionOffsets left, MotionOffsets right)
    {
        return new MotionOffsets(
            left.XMeters + right.XMeters,
            left.YMeters + right.YMeters,
            left.ZMeters + right.ZMeters,
            left.RollRadians + right.RollRadians,
            left.PitchRadians + right.PitchRadians,
            left.YawRadians + right.YawRadians);
    }

    private static double Lerp(double from, double to, double amount)
    {
        return from + ((to - from) * amount);
    }
}
