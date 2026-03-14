using System.Text.Json;

namespace ReachTether.Server.Services;

public sealed class ToolExecutionService(
    ISmartyModeService smartyModeService,
    IMemoryRetrievalService memoryRetrievalService,
    IMemoryPromotionService memoryPromotionService,
    ISessionStore sessionStore) : IToolExecutionService
{
    public async Task<RemoteToolExecutionResponse> ExecuteAsync(RemoteToolExecutionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return request.ToolName switch
            {
                "scheduler" => BuildStub("scheduler", "Scheduler integration is not implemented yet."),
                "kinect_shot" => BuildStub("kinect_shot", "Kinect capture is not implemented yet."),
                "smarty_mode" => await ExecuteSmartyModeAsync(request, cancellationToken),
                "memory_query" => await ExecuteMemoryQueryAsync(request, cancellationToken),
                "memory_search" => await ExecuteMemorySearchAsync(request, cancellationToken),
                "memory_archive" => await ExecuteMemoryArchiveAsync(request, cancellationToken),
                "memory_restore" => await ExecuteMemoryRestoreAsync(request, cancellationToken),
                "memory_promote" => await ExecuteMemoryPromoteAsync(request, cancellationToken),
                "memory_reindex" => await ExecuteMemoryReindexAsync(request, cancellationToken),
                _ => BuildFailure($"Unsupported remote tool '{request.ToolName}'.")
            };
        }
        catch (Exception ex)
        {
            return BuildFailure(ex.Message);
        }
    }

    private static RemoteToolExecutionResponse BuildStub(string toolName, string message)
    {
        var payload = JsonSerializer.Serialize(new
        {
            ok = true,
            tool = toolName,
            stub = true,
            message
        });

        return new RemoteToolExecutionResponse(true, payload, null, []);
    }

    private static RemoteToolExecutionResponse BuildFailure(string message)
    {
        var payload = JsonSerializer.Serialize(new
        {
            ok = false,
            error = message
        });

        return new RemoteToolExecutionResponse(false, payload, message, []);
    }

    private async Task<RemoteToolExecutionResponse> ExecuteSmartyModeAsync(
        RemoteToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var prompt = ExtractPrompt(request.ArgumentsJson);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return BuildFailure("smarty_mode requires a non-empty 'prompt'.");
        }

        var answer = await smartyModeService.ExecuteAsync(prompt, cancellationToken);
        var payload = JsonSerializer.Serialize(new
        {
            ok = true,
            prompt,
            answer
        });

        return new RemoteToolExecutionResponse(true, payload, null, []);
    }

    private async Task<RemoteToolExecutionResponse> ExecuteMemoryQueryAsync(
        RemoteToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var query = ExtractStringArgument(request.ArgumentsJson, "query");
        if (string.IsNullOrWhiteSpace(query))
        {
            return BuildFailure("memory_query requires a non-empty 'query'.");
        }

        var response = await memoryRetrievalService.QueryAsync(
            new KnowledgeQueryRequest(request.SessionId, query, 4),
            cancellationToken);
        var payload = JsonSerializer.Serialize(new
        {
            ok = true,
            query,
            hits = response.Hits,
            sessionSummary = response.SessionSummary
        });
        return new RemoteToolExecutionResponse(true, payload, null, []);
    }

    private async Task<RemoteToolExecutionResponse> ExecuteMemorySearchAsync(
        RemoteToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var query = ExtractStringArgument(request.ArgumentsJson, "query");
        var hits = await sessionStore.SearchMemoryForAdminAsync(request.SessionId, query, includeArchived: true, topK: 10, cancellationToken);
        var payload = JsonSerializer.Serialize(new
        {
            ok = true,
            hits = hits.Select(static hit => new
            {
                hit.MemoryId,
                hit.Title,
                Summary = hit.Summary ?? MemoryPromotionService.Summarize(hit.Content, 180),
                hit.Kind,
                hit.Scope,
                hit.IsArchived
            })
        });
        return new RemoteToolExecutionResponse(true, payload, null, []);
    }

    private async Task<RemoteToolExecutionResponse> ExecuteMemoryArchiveAsync(
        RemoteToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var memoryId = ExtractStringArgument(request.ArgumentsJson, "memoryId");
        if (string.IsNullOrWhiteSpace(memoryId))
        {
            return BuildFailure("memory_archive requires a non-empty 'memoryId'.");
        }

        var response = await sessionStore.ArchiveMemoryAsync(memoryId, cancellationToken);
        return BuildJsonResponse(response);
    }

    private async Task<RemoteToolExecutionResponse> ExecuteMemoryRestoreAsync(
        RemoteToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var memoryId = ExtractStringArgument(request.ArgumentsJson, "memoryId");
        if (string.IsNullOrWhiteSpace(memoryId))
        {
            return BuildFailure("memory_restore requires a non-empty 'memoryId'.");
        }

        var response = await sessionStore.RestoreMemoryAsync(memoryId, cancellationToken);
        return BuildJsonResponse(response);
    }

    private async Task<RemoteToolExecutionResponse> ExecuteMemoryPromoteAsync(
        RemoteToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var promoteRequest = JsonSerializer.Deserialize<PromoteMemoryRequest>(request.ArgumentsJson, JsonSerializerOptions.Web);
        if (promoteRequest is null)
        {
            return BuildFailure("memory_promote requires a valid promotion payload.");
        }

        var response = await memoryPromotionService.PromoteAsync(promoteRequest with { SessionId = request.SessionId }, cancellationToken);
        return BuildJsonResponse(response);
    }

    private async Task<RemoteToolExecutionResponse> ExecuteMemoryReindexAsync(
        RemoteToolExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var reindexRequest = JsonSerializer.Deserialize<ReindexMemoryRequest>(request.ArgumentsJson, JsonSerializerOptions.Web)
            ?? new ReindexMemoryRequest(request.SessionId, null);
        var response = await memoryPromotionService.ReindexAsync(reindexRequest with { SessionId = request.SessionId }, cancellationToken);
        return BuildJsonResponse(response);
    }

    private static RemoteToolExecutionResponse BuildJsonResponse<T>(T payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return new RemoteToolExecutionResponse(true, json, null, []);
    }

    private static string? ExtractPrompt(string argumentsJson)
        => ExtractStringArgument(argumentsJson, "prompt");

    private static string? ExtractStringArgument(string argumentsJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(argumentsJson);
        if (!document.RootElement.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.GetString()?.Trim();
    }
}
