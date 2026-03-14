internal interface IReachTetherServerClient
{
    Task<StartOrResumeSessionResponse> StartOrResumeSessionAsync(
        StartOrResumeSessionRequest request,
        CancellationToken cancellationToken);

    Task<PersistSessionTurnResponse> PersistSessionTurnAsync(
        PersistSessionTurnRequest request,
        CancellationToken cancellationToken);

    Task<KnowledgeQueryResponse> QueryKnowledgeAsync(
        KnowledgeQueryRequest request,
        CancellationToken cancellationToken);

    Task<PromoteMemoryResponse> PromoteMemoryAsync(
        PromoteMemoryRequest request,
        CancellationToken cancellationToken);

    Task<ReindexMemoryResponse> ReindexMemoryAsync(
        ReindexMemoryRequest request,
        CancellationToken cancellationToken);

    Task<RemoteToolExecutionResponse> ExecuteToolAsync(
        RemoteToolExecutionRequest request,
        CancellationToken cancellationToken);

    Task<SnapshotUploadResponse> UploadSnapshotAsync(
        SnapshotUploadRequest request,
        CancellationToken cancellationToken);
}
