internal interface ILookAtProjector
{
    HeadPose Project(double normalizedX, double normalizedY, CameraCalibration calibration);
}
