using System.Text.Json;

namespace ReachTether.Server.Services;

public sealed class ToolExecutionService(ISmartyModeService smartyModeService) : IToolExecutionService
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

    private static string? ExtractPrompt(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(argumentsJson);
        if (!document.RootElement.TryGetProperty("prompt", out var property))
        {
            return null;
        }

        return property.GetString()?.Trim();
    }
}

