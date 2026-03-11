namespace ReachyMini.Sdk.Models;

/// <summary>
/// Details about a successful camera pipeline warmup.
/// </summary>
public sealed record CameraWarmupResult(
    string Backend,
    string PipelineDescription,
    DateTimeOffset WarmedAt);
