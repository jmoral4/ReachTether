namespace ReachTether.Server.Models;

public sealed record SnapshotArtifact(
    string ArtifactId,
    string SessionId,
    string TurnId,
    string ToolCallId,
    string ToolName,
    string Source,
    string? Question,
    string ContentType,
    DateTimeOffset CapturedAt,
    DateTimeOffset CreatedAt,
    string FilePath,
    IReadOnlyDictionary<string, string> Metadata);

