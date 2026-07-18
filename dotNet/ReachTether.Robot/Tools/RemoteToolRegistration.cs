internal sealed class RemoteToolRegistration(
    string toolName,
    string description,
    object parametersSchema,
    Func<RobotAppOptions, bool> isEnabled,
    IReachTetherServerClient serverClient,
    RobotAppOptions options) : IToolRegistration
{
    public string ToolName => toolName;
    public bool IsEnabled => isEnabled(options);

    public ToolDefinition LegacyDefinition { get; } = new(
        toolName,
        description,
        parametersSchema,
        Strict: true);

    public RealtimeToolDefinition RealtimeDefinition { get; } = new(
        toolName,
        description,
        BinaryData.FromObjectAsJson(parametersSchema));

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request, CancellationToken cancellationToken)
    {
        if (!options.Server.Enabled || !options.Tools.EnableRemoteTools)
        {
            return BuildFailure("Remote tools are disabled.");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.Tools.RemoteTimeoutSeconds));

            var response = await serverClient.ExecuteToolAsync(
                new RemoteToolExecutionRequest(
                    request.ToolName,
                    request.ArgumentsJson,
                    request.SessionId,
                    request.TurnId,
                    request.ToolCallId,
                    request.InvocationSource.ToString()),
                timeoutCts.Token);

            var artifacts = response.Artifacts?
                .Select(static artifact => new ToolArtifact(
                    artifact.ArtifactId,
                    artifact.Kind,
                    artifact.Source,
                    artifact.ContentType,
                    artifact.CapturedAt,
                    artifact.Metadata ?? new Dictionary<string, string>(),
                    BinaryContent: null,
                    FileName: null,
                    RemoteArtifactId: artifact.ArtifactId,
                    RemoteContentUrl: artifact.ContentUrl))
                .ToArray() ?? [];

            return new ToolExecutionResult(
                response.OutputJson,
                [],
                [],
                artifacts,
                response.Ok,
                response.ErrorMessage);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BuildFailure($"Remote tool '{request.ToolName}' timed out.");
        }
        catch (Exception ex)
        {
            return BuildFailure($"Remote tool '{request.ToolName}' failed: {ex.Message}");
        }
    }

    private static ToolExecutionResult BuildFailure(string message)
    {
        return new ToolExecutionResult(
            OutputJson: $"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(message)}}}",
            FollowUpMessages: [],
            RealtimeInputs: [],
            Artifacts: [],
            Succeeded: false,
            ErrorMessage: message);
    }
}
