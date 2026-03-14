using OpenAI.Chat;

internal enum ToolInvocationSource
{
    LegacyChat,
    Realtime
}

internal sealed record ToolExecutionRequest(
    string ToolCallId,
    string ToolName,
    string ArgumentsJson,
    string SessionId,
    string TurnId,
    ToolInvocationSource InvocationSource);

internal sealed record ToolArtifact(
    string ArtifactId,
    string Kind,
    string Source,
    string ContentType,
    DateTimeOffset CapturedAt,
    IReadOnlyDictionary<string, string> Metadata,
    byte[]? BinaryContent = null,
    string? FileName = null,
    string? RemoteArtifactId = null,
    string? RemoteContentUrl = null);

internal sealed record RealtimeToolDefinition(
    string Name,
    string Description,
    BinaryData ParametersSchema);

internal sealed record ToolExecutionResult(
    string OutputJson,
    IReadOnlyList<ChatMessage> FollowUpMessages,
    IReadOnlyList<BinaryData> RealtimeCommands,
    IReadOnlyList<ToolArtifact> Artifacts,
    bool Succeeded,
    string? ErrorMessage = null);

