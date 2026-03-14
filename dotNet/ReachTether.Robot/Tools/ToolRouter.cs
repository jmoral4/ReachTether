using System.Text;

internal sealed class ToolRouter(
    IEnumerable<IToolRegistration> registrations,
    IToolArtifactPublisher artifactPublisher) : IToolRouter
{
    private readonly IToolRegistration[] tools = registrations
        .Where(static tool => tool.IsEnabled)
        .ToArray();

    public bool HasAvailableTools => tools.Length > 0;

    public IReadOnlyList<ToolDefinition> GetLegacyToolDefinitions()
        => tools.Select(static tool => tool.LegacyDefinition).ToArray();

    public IReadOnlyList<RealtimeToolDefinition> GetRealtimeToolDefinitions()
        => tools.Select(static tool => tool.RealtimeDefinition).ToArray();

    public string BuildToolUsageGuidance()
    {
        if (tools.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("### TOOL AWARENESS");
        builder.AppendLine("- Use tools when they can verify facts or complete tasks more reliably than guessing.");
        builder.AppendLine("- Prefer tool results over assumptions when the request depends on current state, images, or external actions.");

        foreach (var tool in tools)
        {
            builder.Append("- `");
            builder.Append(tool.LegacyDefinition.Name);
            builder.Append("`: ");
            builder.AppendLine(tool.LegacyDefinition.Description);
        }

        return builder.ToString().Trim();
    }

    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request, CancellationToken cancellationToken)
    {
        var tool = tools.FirstOrDefault(candidate =>
            string.Equals(candidate.ToolName, request.ToolName, StringComparison.OrdinalIgnoreCase));

        if (tool is null)
        {
            return new ToolExecutionResult(
                OutputJson: $"{{\"ok\":false,\"error\":\"Unsupported tool '{request.ToolName}'.\"}}",
                FollowUpMessages: [],
                RealtimeCommands: [],
                Artifacts: [],
                Succeeded: false,
                ErrorMessage: $"Unsupported tool '{request.ToolName}'.");
        }

        var execution = await tool.ExecuteAsync(request, cancellationToken);
        if (execution.Artifacts.Count == 0)
        {
            return execution;
        }

        var publishedArtifacts = await artifactPublisher.PublishAsync(request, execution.Artifacts, cancellationToken);
        return execution with { Artifacts = publishedArtifacts };
    }
}

