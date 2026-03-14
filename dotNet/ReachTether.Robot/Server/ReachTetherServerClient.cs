using System.Net.Http.Json;

internal sealed class ReachTetherServerClient(HttpClient httpClient) : IReachTetherServerClient
{
    public async Task<StartOrResumeSessionResponse> StartOrResumeSessionAsync(
        StartOrResumeSessionRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/sessions/start-or-resume", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StartOrResumeSessionResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Server returned an empty session start/resume response.");
    }

    public async Task<PersistSessionTurnResponse> PersistSessionTurnAsync(
        PersistSessionTurnRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/session-turns", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PersistSessionTurnResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Server returned an empty session turn persistence response.");
    }

    public async Task<KnowledgeQueryResponse> QueryKnowledgeAsync(
        KnowledgeQueryRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/knowledge/query", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<KnowledgeQueryResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Server returned an empty knowledge query response.");
    }

    public async Task<PromoteMemoryResponse> PromoteMemoryAsync(
        PromoteMemoryRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/memory/promote", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PromoteMemoryResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Server returned an empty memory promote response.");
    }

    public async Task<ReindexMemoryResponse> ReindexMemoryAsync(
        ReindexMemoryRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/memory/reindex", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ReindexMemoryResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Server returned an empty memory reindex response.");
    }

    public async Task<RemoteToolExecutionResponse> ExecuteToolAsync(
        RemoteToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/tools/execute", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RemoteToolExecutionResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Server returned an empty tool execution response.");
    }

    public async Task<SnapshotUploadResponse> UploadSnapshotAsync(
        SnapshotUploadRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/snapshots", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SnapshotUploadResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Server returned an empty snapshot upload response.");
    }
}
