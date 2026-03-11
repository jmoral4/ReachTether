namespace ReachyMini.Sdk.Models;

/// <summary>
/// JPEG snapshot captured from Reachy Mini.
/// </summary>
public sealed record CameraSnapshot(
    byte[] ImageBytes,
    string MediaType,
    DateTimeOffset CapturedAt,
    CameraCaptureStats Stats);

/// <summary>
/// Metrics captured during a single snapshot operation.
/// </summary>
public sealed record CameraCaptureStats(
    string Backend,
    int Width,
    int Height,
    int Channels,
    int RawBytes,
    int EncodedBytes,
    double CaptureDurationMs,
    double EncodeDurationMs,
    double TotalDurationMs);
