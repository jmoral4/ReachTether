using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;

internal sealed class LatestFrameCameraSource(
    ICameraSnapshotProvider snapshotProvider,
    RobotAppOptions options,
    ILogger<LatestFrameCameraSource> logger) : BackgroundService, ICameraSource
{
    private readonly RobotAppOptions.VisionSettings _vision = options.Vision;
    private readonly object _sync = new();
    private VideoFrame? _latestFrame;

    public bool TryGetLatestFrame(out VideoFrame? frame)
    {
        lock (_sync)
        {
            frame = _latestFrame;
            return frame is not null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_vision.Enabled || !_vision.FaceTrackingEnabled)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, _vision.FaceTrackingCameraHz));
        logger.LogInformation("Latest camera source started at {CameraHz} Hz.", _vision.FaceTrackingCameraHz);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await snapshotProvider.CaptureSnapshotAsync(
                    bypassCache: true,
                    cancellationToken: stoppingToken);
                if (snapshot is not null)
                {
                    var frame = ToVideoFrame(snapshot);
                    lock (_sync)
                    {
                        _latestFrame = frame;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Camera frame capture failed.");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private static VideoFrame ToVideoFrame(VisionCameraSnapshot snapshot)
    {
        var info = Image.Identify(snapshot.ImageBytes);
        var width = info?.Width ?? 0;
        var height = info?.Height ?? 0;
        return new VideoFrame(snapshot.ImageBytes, snapshot.MediaType, snapshot.CapturedAt, width, height);
    }
}
