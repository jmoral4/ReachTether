using ReachyMini.Sdk;

internal sealed class CameraSnapshotService(
    ReachyMiniClient reachyClient,
    RobotAppOptions options) : ICameraSnapshotProvider
{
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private VisionCameraSnapshot? cachedSnapshot;
    private DateTimeOffset cacheExpiresAtUtc = DateTimeOffset.MinValue;

    public async Task<VisionCameraSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Vision.Enabled)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (options.Vision.SnapshotCacheMs > 0 && cachedSnapshot is not null && now < cacheExpiresAtUtc)
        {
            return cachedSnapshot;
        }

        await captureGate.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (options.Vision.SnapshotCacheMs > 0 && cachedSnapshot is not null && now < cacheExpiresAtUtc)
            {
                return cachedSnapshot;
            }

            var snapshot = await reachyClient.Camera.CaptureSnapshotAsync(cancellationToken);
            var visionSnapshot = new VisionCameraSnapshot(
                snapshot.ImageBytes,
                string.IsNullOrWhiteSpace(snapshot.MediaType) ? "image/jpeg" : snapshot.MediaType,
                snapshot.CapturedAt);

            if (options.Vision.SnapshotCacheMs > 0)
            {
                cachedSnapshot = visionSnapshot;
                cacheExpiresAtUtc = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(options.Vision.SnapshotCacheMs);
            }
            else
            {
                cachedSnapshot = null;
                cacheExpiresAtUtc = DateTimeOffset.MinValue;
            }

            return visionSnapshot;
        }
        finally
        {
            captureGate.Release();
        }
    }
}
