using System.Text.Json.Serialization;

namespace ReachTether.Server;

public sealed record SessionMetadataEntry(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] string Value);

public sealed record PromptRecentTurn(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

public sealed record RetrievedMemoryItem(
    [property: JsonPropertyName("memoryId")] string MemoryId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("summaryOrSnippet")] string SummaryOrSnippet,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("sourceTurnId")] string? SourceTurnId,
    [property: JsonPropertyName("score")] double Score);

public sealed record SessionSummaryDescriptor(
    [property: JsonPropertyName("memoryId")] string MemoryId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

public sealed record ActiveProfileDescriptor(
    [property: JsonPropertyName("profileId")] string ProfileId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

public sealed record PendingSystemEventDescriptor(
    [property: JsonPropertyName("eventId")] string EventId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("summary")] string Summary);

public sealed record StartOrResumeSessionRequest(
    [property: JsonPropertyName("sessionKey")] string SessionKey,
    [property: JsonPropertyName("userId")] string UserId,
    [property: JsonPropertyName("lane")] string Lane,
    [property: JsonPropertyName("activePersonalityId")] string ActivePersonalityId,
    [property: JsonPropertyName("metadata")] IReadOnlyList<SessionMetadataEntry>? Metadata);

public sealed record StartOrResumeSessionResponse(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("resumed")] bool Resumed,
    [property: JsonPropertyName("activePersonalityId")] string ActivePersonalityId,
    [property: JsonPropertyName("activeProfile")] ActiveProfileDescriptor? ActiveProfile,
    [property: JsonPropertyName("sessionSummary")] SessionSummaryDescriptor? SessionSummary,
    [property: JsonPropertyName("recentTurns")] IReadOnlyList<PromptRecentTurn> RecentTurns,
    [property: JsonPropertyName("pendingSystemEvents")] IReadOnlyList<PendingSystemEventDescriptor> PendingSystemEvents);

public sealed record PersistedToolCallDescriptor(
    [property: JsonPropertyName("toolCallId")] string ToolCallId,
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("argumentsJson")] string ArgumentsJson,
    [property: JsonPropertyName("outputJson")] string? OutputJson,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

public sealed record PersistedArtifactDescriptor(
    [property: JsonPropertyName("artifactId")] string ArtifactId,
    [property: JsonPropertyName("turnId")] string? TurnId,
    [property: JsonPropertyName("toolCallId")] string? ToolCallId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("contentUrlOrPath")] string ContentUrlOrPath,
    [property: JsonPropertyName("metadataJson")] string? MetadataJson,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

public sealed record PersistSessionTurnRequest(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("turnId")] string TurnId,
    [property: JsonPropertyName("userText")] string? UserText,
    [property: JsonPropertyName("assistantText")] string? AssistantText,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("correlationId")] string? CorrelationId,
    [property: JsonPropertyName("activePersonalityId")] string? ActivePersonalityId,
    [property: JsonPropertyName("toolCalls")] IReadOnlyList<PersistedToolCallDescriptor>? ToolCalls,
    [property: JsonPropertyName("artifacts")] IReadOnlyList<PersistedArtifactDescriptor>? Artifacts);

public sealed record PersistSessionTurnResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("storedTurnIds")] IReadOnlyList<string> StoredTurnIds,
    [property: JsonPropertyName("sessionSummary")] SessionSummaryDescriptor? SessionSummary);

public sealed record KnowledgeQueryRequest(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("topK")] int? TopK);

public sealed record KnowledgeQueryResponse(
    [property: JsonPropertyName("hits")] IReadOnlyList<RetrievedMemoryItem> Hits,
    [property: JsonPropertyName("activeProfile")] ActiveProfileDescriptor? ActiveProfile,
    [property: JsonPropertyName("sessionSummary")] SessionSummaryDescriptor? SessionSummary,
    [property: JsonPropertyName("pendingSystemEvents")] IReadOnlyList<PendingSystemEventDescriptor> PendingSystemEvents);

public sealed record PromoteMemoryRequest(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("profileId")] string? ProfileId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("attributeName")] string? AttributeName,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("normalizedValue")] string? NormalizedValue,
    [property: JsonPropertyName("sourceTurnId")] string? SourceTurnId,
    [property: JsonPropertyName("importance")] double Importance);

public sealed record PromoteMemoryResponse(
    [property: JsonPropertyName("memoryId")] string MemoryId,
    [property: JsonPropertyName("created")] bool Created,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

public sealed record MemorySearchResponse(
    [property: JsonPropertyName("hits")] IReadOnlyList<RetrievedMemoryItem> Hits);

public sealed record ArchiveMemoryResponse(
    [property: JsonPropertyName("memoryId")] string MemoryId,
    [property: JsonPropertyName("archived")] bool Archived,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

public sealed record RestoreMemoryResponse(
    [property: JsonPropertyName("memoryId")] string MemoryId,
    [property: JsonPropertyName("archived")] bool Archived,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

public sealed record ReindexMemoryRequest(
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("memoryIds")] IReadOnlyList<string>? MemoryIds);

public sealed record ReindexMemoryResponse(
    [property: JsonPropertyName("processed")] int Processed,
    [property: JsonPropertyName("updated")] int Updated);

public sealed record RemoteToolExecutionRequest(
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("argumentsJson")] string ArgumentsJson,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("turnId")] string TurnId,
    [property: JsonPropertyName("toolCallId")] string ToolCallId,
    [property: JsonPropertyName("invocationSource")] string InvocationSource);

public sealed record RemoteArtifactDescriptor(
    [property: JsonPropertyName("artifactId")] string ArtifactId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("capturedAt")] DateTimeOffset CapturedAt,
    [property: JsonPropertyName("contentUrl")] string? ContentUrl,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata);

public sealed record RemoteToolExecutionResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("outputJson")] string OutputJson,
    [property: JsonPropertyName("errorMessage")] string? ErrorMessage,
    [property: JsonPropertyName("artifacts")] IReadOnlyList<RemoteArtifactDescriptor>? Artifacts);

public sealed record SnapshotUploadRequest(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("turnId")] string TurnId,
    [property: JsonPropertyName("toolCallId")] string ToolCallId,
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("question")] string? Question,
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("capturedAt")] DateTimeOffset CapturedAt,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("base64Content")] string Base64Content,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, string>? Metadata);

public sealed record SnapshotUploadResponse(
    [property: JsonPropertyName("artifactId")] string ArtifactId,
    [property: JsonPropertyName("contentUrl")] string ContentUrl,
    [property: JsonPropertyName("storedAt")] DateTimeOffset StoredAt);
