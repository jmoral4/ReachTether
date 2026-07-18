using System.Text.Json;

internal sealed record PromptHydrationResult(
    string SessionId,
    bool Resumed,
    ActiveProfileDescriptor? ActiveProfile,
    SessionSummaryDescriptor? SessionSummary,
    IReadOnlyList<PromptRecentTurn> RecentTurns,
    IReadOnlyList<RetrievedMemoryItem> RetrievedMemory,
    IReadOnlyList<PendingSystemEventDescriptor> PendingSystemEvents);

internal interface IServerSessionCoordinator
{
    Task<PromptHydrationResult> StartOrResumeAsync(string activePersonalityId, CancellationToken cancellationToken);

    Task<PromptHydrationResult> HydratePromptAsync(
        string sessionId,
        string query,
        CancellationToken cancellationToken);

    Task PersistTurnAsync(
        PersistSessionTurnRequest request,
        CancellationToken cancellationToken);
}

internal sealed class ServerSessionCoordinator(
    IReachTetherServerClient serverClient,
    RobotAppOptions options) : IServerSessionCoordinator
{
    public async Task<PromptHydrationResult> StartOrResumeAsync(string activePersonalityId, CancellationToken cancellationToken)
    {
        if (!options.Server.Enabled)
        {
            return EmptyHydration(Guid.NewGuid().ToString("n"));
        }

        var response = await serverClient.StartOrResumeSessionAsync(
            new StartOrResumeSessionRequest(
                options.Server.SessionKey,
                options.Server.UserId,
                options.Server.Lane,
                activePersonalityId,
                [
                    new SessionMetadataEntry("voicePipeline", options.VoicePipeline),
                    new SessionMetadataEntry("chatModel", options.ChatModel)
                ]),
            cancellationToken);

        return new PromptHydrationResult(
            response.SessionId,
            response.Resumed,
            response.ActiveProfile,
            response.SessionSummary,
            response.RecentTurns,
            [],
            response.PendingSystemEvents);
    }

    public async Task<PromptHydrationResult> HydratePromptAsync(
        string sessionId,
        string query,
        CancellationToken cancellationToken)
    {
        if (!options.Server.Enabled)
        {
            return EmptyHydration(sessionId);
        }

        var response = await serverClient.QueryKnowledgeAsync(
            new KnowledgeQueryRequest(sessionId, query, 4),
            cancellationToken);
        return new PromptHydrationResult(
            sessionId,
            true,
            response.ActiveProfile,
            response.SessionSummary,
            [],
            response.Hits,
            response.PendingSystemEvents);
    }

    public Task PersistTurnAsync(PersistSessionTurnRequest request, CancellationToken cancellationToken)
        => options.Server.Enabled
            ? serverClient.PersistSessionTurnAsync(request, cancellationToken)
            : Task.CompletedTask;

    private static PromptHydrationResult EmptyHydration(string sessionId)
        => new(sessionId, false, null, null, [], [], []);

    public static PersistedArtifactDescriptor ToPersistedArtifactDescriptor(string turnId, ToolArtifact artifact, string? toolCallId = null)
    {
        var contentLocation = artifact.RemoteContentUrl
            ?? artifact.RemoteArtifactId
            ?? artifact.FileName
            ?? artifact.ArtifactId;

        return new PersistedArtifactDescriptor(
            artifact.RemoteArtifactId ?? artifact.ArtifactId,
            turnId,
            toolCallId,
            artifact.Kind,
            artifact.ContentType,
            contentLocation,
            JsonSerializer.Serialize(artifact.Metadata),
            artifact.CapturedAt);
    }
}
