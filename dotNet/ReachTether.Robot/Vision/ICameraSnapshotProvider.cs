internal interface ICameraSnapshotProvider
{
    Task<VisionCameraSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default);
}
