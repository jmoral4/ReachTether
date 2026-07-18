internal sealed record TrackingCommand(
    HeadPose DesiredPose,
    double Confidence,
    DateTimeOffset TimestampUtc);
