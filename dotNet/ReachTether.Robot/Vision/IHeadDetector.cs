internal interface IHeadDetector
{
    Task<DetectionResult?> DetectAsync(
        VideoFrame frame,
        CancellationToken cancellationToken = default);
}
