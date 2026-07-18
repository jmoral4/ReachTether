internal sealed record DetectionResult(
    double CenterX,
    double CenterY,
    double Confidence,
    double AreaNormalized,
    DateTimeOffset TimestampUtc);
