internal sealed record TrackingObservation(
    double CenterX,
    double CenterY,
    double Confidence,
    double RelativeSize,
    DateTimeOffset TimestampUtc);
