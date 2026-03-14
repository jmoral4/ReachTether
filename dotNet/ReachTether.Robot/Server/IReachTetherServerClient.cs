internal interface IReachTetherServerClient
{
    Task<RemoteToolExecutionResponse> ExecuteToolAsync(
        RemoteToolExecutionRequest request,
        CancellationToken cancellationToken);

    Task<SnapshotUploadResponse> UploadSnapshotAsync(
        SnapshotUploadRequest request,
        CancellationToken cancellationToken);
}

