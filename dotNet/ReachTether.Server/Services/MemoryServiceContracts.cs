using System.Text.Json;

namespace ReachTether.Server.Services;

public interface ISqliteConnectionFactory
{
    string DatabasePath { get; }
    Task<Microsoft.Data.Sqlite.SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken);
}

public interface ISqliteSchemaInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken);
}

public interface ISessionStore
{
    Task<StartOrResumeSessionResponse> StartOrResumeSessionAsync(
        StartOrResumeSessionRequest request,
        CancellationToken cancellationToken);

    Task<PersistSessionTurnResponse> PersistSessionTurnAsync(
        PersistSessionTurnRequest request,
        CancellationToken cancellationToken);

    Task LinkSessionToProfileAsync(
        string sessionId,
        string? profileId,
        CancellationToken cancellationToken);

    Task RecordArtifactMetadataAsync(
        PersistedArtifactDescriptor artifact,
        string sessionId,
        CancellationToken cancellationToken);

    Task<SessionSummaryDescriptor?> GetSessionSummaryAsync(
        string sessionId,
        CancellationToken cancellationToken);

    Task<ActiveProfileDescriptor?> GetActiveProfileAsync(
        string sessionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PromptRecentTurn>> GetRecentTurnsAsync(
        string sessionId,
        int count,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PendingSystemEventDescriptor>> GetPendingSystemEventsAsync(
        string sessionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredMemoryRecord>> SearchMemoryByTextAsync(
        string sessionId,
        string query,
        int topK,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredMemoryRecord>> GetActiveMemoryRecordsAsync(
        string sessionId,
        CancellationToken cancellationToken);

    Task<PromoteMemoryResponse> UpsertMemoryAsync(
        PromoteMemoryRequest request,
        string? existingMemoryId,
        CancellationToken cancellationToken);

    Task<string?> FindExistingMemoryIdAsync(
        string sessionId,
        string scope,
        string kind,
        string? attributeName,
        string? profileId,
        CancellationToken cancellationToken);

    Task UpsertMemoryVectorAsync(
        string memoryId,
        EmbeddingVectorResult embedding,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredMemoryRecord>> GetProfileMemoryRecordsAsync(
        string profileId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredProfileRecord>> FindProfilesByNormalizedNameAsync(
        string normalizedName,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredProfileRecord>> ListProfilesAsync(
        CancellationToken cancellationToken);

    Task<string?> GetMostRecentlyActiveProfileIdAsync(
        CancellationToken cancellationToken);

    Task<StoredProfileRecord> CreateProfileAsync(
        string displayName,
        string normalizedName,
        CancellationToken cancellationToken);

    Task UpdateProfileSummaryAsync(
        string profileId,
        string displayName,
        string summary,
        CancellationToken cancellationToken);

    Task UpsertPendingSystemEventAsync(
        string sessionId,
        string eventKind,
        string title,
        string summary,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredMemoryRecord>> SearchMemoryForAdminAsync(
        string? sessionId,
        string? query,
        bool includeArchived,
        int topK,
        CancellationToken cancellationToken);

    Task<ArchiveMemoryResponse> ArchiveMemoryAsync(string memoryId, CancellationToken cancellationToken);
    Task<RestoreMemoryResponse> RestoreMemoryAsync(string memoryId, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredMemoryRecord>> GetMemoryRecordsForReindexAsync(
        string? sessionId,
        IReadOnlyList<string>? memoryIds,
        CancellationToken cancellationToken);
}

public interface IMemoryEmbeddingProvider
{
    Task<EmbeddingVectorResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken);
}

public interface IMemoryRetrievalService
{
    Task<KnowledgeQueryResponse> QueryAsync(KnowledgeQueryRequest request, CancellationToken cancellationToken);

    IReadOnlyList<RetrievedMemoryItem> FuseResults(
        IReadOnlyList<StoredMemoryRecord> ftsMatches,
        IReadOnlyList<(StoredMemoryRecord Memory, double Score)> vectorMatches,
        int topK);
}

public interface IMemoryPromotionService
{
    Task ProcessPersistedTurnAsync(PersistSessionTurnRequest request, CancellationToken cancellationToken);

    Task<PromoteMemoryResponse> PromoteAsync(PromoteMemoryRequest request, CancellationToken cancellationToken);

    Task<ReindexMemoryResponse> ReindexAsync(ReindexMemoryRequest request, CancellationToken cancellationToken);
}

public interface IUserFactExtractionService
{
    Task<UserFactExtractionResult> ExtractAsync(
        PersistSessionTurnRequest request,
        CancellationToken cancellationToken);

    Task<string> SummarizeSessionAsync(
        IReadOnlyList<PromptRecentTurn> recentTurns,
        CancellationToken cancellationToken);
}

public sealed record EmbeddingRequest(string Input, string? Hint = null);

public sealed record EmbeddingVectorResult(
    string Provider,
    string Model,
    int Dimensions,
    IReadOnlyList<float> Embedding);

public sealed record StoredMemoryRecord(
    string MemoryId,
    string SessionId,
    string? ProfileId,
    string Scope,
    string Kind,
    string? AttributeName,
    string Title,
    string Content,
    string? Summary,
    string? NormalizedValue,
    string? SourceTurnId,
    double Importance,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastAccessedAt,
    bool IsArchived,
    string? EmbeddingProvider,
    string? EmbeddingModel,
    int? EmbeddingDimensions,
    IReadOnlyList<float>? Embedding,
    double TextScore = 0);

public sealed record StoredProfileRecord(
    string ProfileId,
    string DisplayName,
    string NormalizedName,
    string Summary,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record UserFactExtractionResult(
    IReadOnlyList<ExtractedFact> Facts,
    string? SessionSummary);

public sealed record ExtractedFact(
    string Kind,
    string Attribute,
    string Value,
    string? NormalizedValue,
    string Stability,
    double Confidence,
    string Evidence,
    string? SupersedesAttribute,
    string? ScopeHint);

internal static class MemoryJson
{
    public static string SerializeMetadata(IReadOnlyDictionary<string, string>? metadata)
        => metadata is null ? "{}" : JsonSerializer.Serialize(metadata);
}
