internal readonly record struct HeadPose(
    double YawRadians,
    double PitchRadians,
    double RollRadians = 0.0)
{
    public static readonly HeadPose Neutral = new(0.0, 0.0, 0.0);
}
