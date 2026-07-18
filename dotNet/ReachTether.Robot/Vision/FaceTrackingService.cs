using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal sealed class FaceTrackingService(
    ICameraSource cameraSource,
    IHeadDetector detector,
    HeadTrackingController controller,
    IMotionOrchestrator motionOrchestrator,
    RobotAppOptions options,
    ILogger<FaceTrackingService> logger) : BackgroundService
{
    private readonly RobotAppOptions.VisionSettings _vision = options.Vision;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        motionOrchestrator.SetFaceTrackingOffsets(MotionOffsets.Zero);

        if (!_vision.Enabled || !_vision.FaceTrackingEnabled)
        {
            logger.LogInformation("Face tracking service is disabled by configuration.");
            return;
        }

        var perceptionInterval = TimeSpan.FromSeconds(1.0 / Math.Max(1, _vision.FaceTrackingHz));
        var controlInterval = TimeSpan.FromSeconds(1.0 / Math.Max(1, _vision.FaceTrackingControlHz));

        logger.LogInformation(
            "Face tracking service started: detectionHz={DetectionHz}, controlHz={ControlHz}, cameraHz={CameraHz}, model={Model}.",
            _vision.FaceTrackingHz,
            _vision.FaceTrackingControlHz,
            _vision.FaceTrackingCameraHz,
            _vision.FaceTrackingModel);

        var perceptionTask = RunPerceptionLoopAsync(perceptionInterval, detector, controller, stoppingToken);
        var controlTask = RunControlLoopAsync(controlInterval, controller, motionOrchestrator, stoppingToken);
        await Task.WhenAll(perceptionTask, controlTask);

        motionOrchestrator.SetFaceTrackingOffsets(MotionOffsets.Zero);
    }

    private async Task RunPerceptionLoopAsync(
        TimeSpan interval,
        IHeadDetector detector,
        HeadTrackingController controller,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!cameraSource.TryGetLatestFrame(out var frame) || frame is null)
                {
                    controller.UpdateObservation(null);
                }
                else
                {
                    var detection = await detector.DetectAsync(frame, stoppingToken);
                    controller.UpdateObservation(detection is null
                        ? null
                        : new TrackingObservation(
                            detection.CenterX,
                            detection.CenterY,
                            detection.Confidence,
                            detection.AreaNormalized,
                            detection.TimestampUtc));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Face tracking perception iteration failed.");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task RunControlLoopAsync(
        TimeSpan interval,
        HeadTrackingController controller,
        IMotionOrchestrator motionOrchestrator,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CameraCalibration calibration;
                if (cameraSource.TryGetLatestFrame(out var frame) && frame is not null && frame.Width > 0 && frame.Height > 0)
                {
                    calibration = new CameraCalibration(
                        frame.Width,
                        frame.Height,
                        _vision.FaceTrackingHorizontalFieldOfViewDegrees,
                        _vision.FaceTrackingVerticalFieldOfViewDegrees);
                }
                else
                {
                    calibration = new CameraCalibration(
                        _vision.Width,
                        _vision.Height,
                        _vision.FaceTrackingHorizontalFieldOfViewDegrees,
                        _vision.FaceTrackingVerticalFieldOfViewDegrees);
                }

                var command = controller.GetTrackingCommand(DateTimeOffset.UtcNow, calibration);
                motionOrchestrator.SetFaceTrackingOffsets(ToMotionOffsets(command.DesiredPose));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Face tracking control iteration failed.");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private MotionOffsets ToMotionOffsets(HeadPose pose)
    {
        var yawRadians = Math.Clamp(
            pose.YawRadians * _vision.FaceTrackingOffsetScale,
            -DegreesToRadians(_vision.FaceTrackingMaxYawDegrees),
            DegreesToRadians(_vision.FaceTrackingMaxYawDegrees));
        var pitchRadians = Math.Clamp(
            pose.PitchRadians * _vision.FaceTrackingOffsetScale,
            -DegreesToRadians(_vision.FaceTrackingMaxPitchDegrees),
            DegreesToRadians(_vision.FaceTrackingMaxPitchDegrees));

        return new MotionOffsets(
            XMeters: 0.0,
            YMeters: 0.0,
            ZMeters: 0.0,
            RollRadians: pose.RollRadians,
            PitchRadians: pitchRadians,
            YawRadians: yawRadians);
    }

    private static double DegreesToRadians(double degrees)
    {
        return degrees * Math.PI / 180.0;
    }
}
