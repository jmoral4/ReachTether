using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReachyMini.Sdk;

internal sealed class VisionStartupProbe(
    ReachyMiniClient reachyClient,
    RobotAppOptions options,
    IHostApplicationLifetime hostApplicationLifetime,
    ILogger<VisionStartupProbe> logger)
{
    public bool IsEnabled => options.Vision.WarmupOnStartup || options.Vision.ProbeOnStartup || options.Vision.ProbeOnly;

    public async Task RunAfterRobotReadyAsync(CancellationToken cancellationToken)
    {
        var vision = options.Vision;
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            if (vision.WarmupOnStartup)
            {
                Console.WriteLine(
                    $"[VisionProbe] Warming camera after robot ready: sourceKind={vision.SourceKind}, sourcePath={vision.SourcePath}, {vision.Width}x{vision.Height}@{vision.Framerate}");

                var warmup = await reachyClient.Camera.WarmupAsync(cancellationToken);
                Console.WriteLine(
                    $"[VisionProbe] Camera warmup succeeded: backend={warmup.Backend}, warmedAt={warmup.WarmedAt:O}");

                logger.LogInformation(
                    "Vision camera warmup succeeded with backend {Backend} at {WarmedAt}.",
                    warmup.Backend,
                    warmup.WarmedAt);

                if (vision.WarmupDelayMs > 0)
                {
                    await Task.Delay(vision.WarmupDelayMs, cancellationToken);
                }
            }

            if (vision.ProbeOnStartup || vision.ProbeOnly)
            {
                Console.WriteLine(
                    $"[VisionProbe] Starting capture probe after robot ready: count={vision.ProbeCaptureCount}");

                for (var index = 0; index < vision.ProbeCaptureCount && !cancellationToken.IsCancellationRequested; index++)
                {
                    var snapshot = await reachyClient.Camera.CaptureSnapshotAsync(cancellationToken);
                    var stats = snapshot.Stats;

                    Console.WriteLine(
                        $"[VisionProbe] Capture {index + 1}/{vision.ProbeCaptureCount}: {stats.Width}x{stats.Height}x{stats.Channels}, rawBytes={stats.RawBytes}, jpegBytes={stats.EncodedBytes}, captureMs={stats.CaptureDurationMs:F2}, encodeMs={stats.EncodeDurationMs:F2}, totalMs={stats.TotalDurationMs:F2}, capturedAt={snapshot.CapturedAt:O}");

                    logger.LogInformation(
                        "Vision probe capture {CaptureIndex}/{CaptureCount}: {Width}x{Height}x{Channels}, rawBytes={RawBytes}, jpegBytes={EncodedBytes}, captureMs={CaptureMs}, encodeMs={EncodeMs}, totalMs={TotalMs}",
                        index + 1,
                        vision.ProbeCaptureCount,
                        stats.Width,
                        stats.Height,
                        stats.Channels,
                        stats.RawBytes,
                        stats.EncodedBytes,
                        stats.CaptureDurationMs,
                        stats.EncodeDurationMs,
                        stats.TotalDurationMs);

                    if (index + 1 < vision.ProbeCaptureCount && vision.ProbeDelayMs > 0)
                    {
                        await Task.Delay(vision.ProbeDelayMs, cancellationToken);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VisionProbe] Capture failed: {ex.Message}");
            logger.LogError(ex, "Vision startup probe failed.");
        }
        finally
        {
            if (vision.ProbeOnly)
            {
                hostApplicationLifetime.StopApplication();
            }
        }
    }
}
