using Microsoft.Extensions.Configuration;

public sealed class RobotRuntimeConfigurationTests
{
    [Fact]
    public void EmptyConfigurationDefaultsToRealtimeVoice()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = RobotAppOptions.FromConfiguration(configuration);

        Assert.Equal("realtime", options.VoicePipeline);
        Assert.True(options.UseRealtimeVoicePipeline);
        Assert.Equal("gpt-realtime-2.1", options.RealtimeModel);
    }

    [Fact]
    public async Task DisabledServerSkipsSessionHydrationAndPersistenceCalls()
    {
        var client = new RecordingServerClient();
        var options = new RobotAppOptions
        {
            Server = new RobotAppOptions.ServerSettings { Enabled = false }
        };
        var coordinator = new ServerSessionCoordinator(client, options);

        var started = await coordinator.StartOrResumeAsync("default", CancellationToken.None);
        var hydrated = await coordinator.HydratePromptAsync(started.SessionId, "hello", CancellationToken.None);
        await coordinator.PersistTurnAsync(
            new PersistSessionTurnRequest(
                started.SessionId,
                "turn-1",
                "hello",
                "hi",
                "realtime",
                options.RealtimeModel,
                null,
                "default",
                [],
                []),
            CancellationToken.None);

        Assert.False(started.Resumed);
        Assert.Equal(started.SessionId, hydrated.SessionId);
        Assert.Empty(hydrated.RetrievedMemory);
        Assert.Equal(0, client.CallCount);
    }

    private sealed class RecordingServerClient : IReachTetherServerClient
    {
        public int CallCount { get; private set; }

        public Task<StartOrResumeSessionResponse> StartOrResumeSessionAsync(
            StartOrResumeSessionRequest request,
            CancellationToken cancellationToken)
            => UnexpectedCall<StartOrResumeSessionResponse>();

        public Task<PersistSessionTurnResponse> PersistSessionTurnAsync(
            PersistSessionTurnRequest request,
            CancellationToken cancellationToken)
            => UnexpectedCall<PersistSessionTurnResponse>();

        public Task<KnowledgeQueryResponse> QueryKnowledgeAsync(
            KnowledgeQueryRequest request,
            CancellationToken cancellationToken)
            => UnexpectedCall<KnowledgeQueryResponse>();

        public Task<PromoteMemoryResponse> PromoteMemoryAsync(
            PromoteMemoryRequest request,
            CancellationToken cancellationToken)
            => UnexpectedCall<PromoteMemoryResponse>();

        public Task<ReindexMemoryResponse> ReindexMemoryAsync(
            ReindexMemoryRequest request,
            CancellationToken cancellationToken)
            => UnexpectedCall<ReindexMemoryResponse>();

        public Task<RemoteToolExecutionResponse> ExecuteToolAsync(
            RemoteToolExecutionRequest request,
            CancellationToken cancellationToken)
            => UnexpectedCall<RemoteToolExecutionResponse>();

        public Task<SnapshotUploadResponse> UploadSnapshotAsync(
            SnapshotUploadRequest request,
            CancellationToken cancellationToken)
            => UnexpectedCall<SnapshotUploadResponse>();

        private Task<T> UnexpectedCall<T>()
        {
            CallCount++;
            return Task.FromException<T>(new InvalidOperationException("Server client should not be called when disabled."));
        }
    }
}
