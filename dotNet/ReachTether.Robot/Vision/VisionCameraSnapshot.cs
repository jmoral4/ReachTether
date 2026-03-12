internal sealed record VisionCameraSnapshot(
    byte[] ImageBytes,
    string MediaType,
    DateTimeOffset CapturedAt);
