internal sealed class PinholeLookAtProjector : ILookAtProjector
{
    public HeadPose Project(double normalizedX, double normalizedY, CameraCalibration calibration)
    {
        var width = Math.Max(2, calibration.Width);
        var height = Math.Max(2, calibration.Height);
        var px = ((Math.Clamp(normalizedX, -1.0, 1.0) + 1.0) * 0.5) * (width - 1);
        var py = ((Math.Clamp(normalizedY, -1.0, 1.0) + 1.0) * 0.5) * (height - 1);
        var cx = (width - 1) * 0.5;
        var cy = (height - 1) * 0.5;
        var fx = width / (2.0 * Math.Tan(DegreesToRadians(calibration.HorizontalFieldOfViewDegrees) * 0.5));
        var fy = height / (2.0 * Math.Tan(DegreesToRadians(calibration.VerticalFieldOfViewDegrees) * 0.5));
        var yaw = Math.Atan((px - cx) / fx);
        var pitch = -Math.Atan((py - cy) / fy);
        return new HeadPose(yaw, pitch);
    }

    private static double DegreesToRadians(double degrees)
    {
        return degrees * Math.PI / 180.0;
    }
}
