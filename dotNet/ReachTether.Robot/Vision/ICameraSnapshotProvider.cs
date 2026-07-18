internal interface ICameraSnapshotProvider
{
    Task<VisionCameraSnapshot?> CaptureSnapshotAsync(
        bool bypassCache = false,
        CancellationToken cancellationToken = default);
}
