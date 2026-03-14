internal interface IToolDefinitionSource
{
    IReadOnlyList<ToolDefinition> GetLegacyToolDefinitions();
    IReadOnlyList<RealtimeToolDefinition> GetRealtimeToolDefinitions();
    string BuildToolUsageGuidance();
}

internal interface IToolExecutor
{
    string ToolName { get; }
    Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request, CancellationToken cancellationToken);
}

internal interface IToolRouter : IToolDefinitionSource
{
    bool HasAvailableTools { get; }
    Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request, CancellationToken cancellationToken);
}

internal interface IToolArtifactPublisher
{
    Task<IReadOnlyList<ToolArtifact>> PublishAsync(
        ToolExecutionRequest request,
        IReadOnlyList<ToolArtifact> artifacts,
        CancellationToken cancellationToken);
}

internal interface IToolRegistration : IToolExecutor
{
    ToolDefinition LegacyDefinition { get; }
    RealtimeToolDefinition RealtimeDefinition { get; }
    bool IsEnabled { get; }
}

