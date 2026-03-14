using System.Net.Http.Json;

internal sealed class ReachTetherServerClient(HttpClient httpClient) : IReachTetherServerClient
{
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

