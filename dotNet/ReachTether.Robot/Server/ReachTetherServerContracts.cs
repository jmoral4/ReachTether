using System.Text.Json.Serialization;

internal sealed record RemoteToolExecutionRequest(
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("argumentsJson")] string ArgumentsJson,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("turnId")] string TurnId,
    [property: JsonPropertyName("toolCallId")] string ToolCallId,
    [property: JsonPropertyName("invocationSource")] string InvocationSource);

internal sealed record RemoteArtifactDescriptor(
    [property: JsonPropertyName("artifactId")] string ArtifactId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("capturedAt")] DateTimeOffset CapturedAt,
    [property: JsonPropertyName("contentUrl")] string? ContentUrl,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata);

internal sealed record RemoteToolExecutionResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("outputJson")] string OutputJson,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage,
    [property: JsonPropertyName("artifacts")] IReadOnlyList<RemoteArtifactDescriptor>? Artifacts);

internal sealed record SnapshotUploadRequest(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("turnId")] string TurnId,
    [property: JsonPropertyName("toolCallId")] string ToolCallId,
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("question")] string? Question,
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("capturedAt")] DateTimeOffset CapturedAt,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("base64Content")] string Base64Content,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata);

internal sealed record SnapshotUploadResponse(
    [property: JsonPropertyName("artifactId")] string ArtifactId,
    [property: JsonPropertyName("contentUrl")] string ContentUrl,
    [property: JsonPropertyName("storedAt")] DateTimeOffset StoredAt);

