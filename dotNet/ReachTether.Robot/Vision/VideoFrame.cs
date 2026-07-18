internal sealed record VideoFrame(
    byte[] ImageBytes,
    string MediaType,
    DateTimeOffset TimestampUtc,
    int Width,
    int Height);
