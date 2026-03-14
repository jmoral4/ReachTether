internal sealed class NullToolArtifactPublisher : IToolArtifactPublisher
{
    public Task<IReadOnlyList<ToolArtifact>> PublishAsync(
        ToolExecutionRequest request,
        IReadOnlyList<ToolArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(artifacts);
    }
}

