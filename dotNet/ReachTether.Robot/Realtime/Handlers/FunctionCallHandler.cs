using Microsoft.Extensions.Logging;
using OpenAI.RealtimeConversation;
using System.ClientModel.Primitives;

internal sealed class FunctionCallHandler(CameraTool cameraTool) : IRealtimeEventHandler
{
    public int Order => 250;

    public async ValueTask<bool> HandleAsync(ConversationUpdate update, RealtimeTurnContext context, CancellationToken ct)
    {
        return update switch
        {
            ConversationItemStreamingFinishedUpdate finished
                when IsFunctionCall(finished.FunctionName, finished.FunctionCallId)
                => await HandleFunctionCallAsync(
                    finished.FunctionName!,
                    finished.FunctionCallId!,
                    finished.FunctionCallArguments,
                    context,
                    ct),

            ConversationItemCreatedUpdate created
                when IsFunctionCall(created.FunctionName, created.FunctionCallId)
                => await HandleFunctionCallAsync(
                    created.FunctionName!,
                    created.FunctionCallId!,
                    created.FunctionCallArguments,
                    context,
                    ct),

            _ => false
        };
    }

    private static bool IsFunctionCall(string? functionName, string? functionCallId)
    {
        return !string.IsNullOrWhiteSpace(functionName)
            && !string.IsNullOrWhiteSpace(functionCallId);
    }

    private async ValueTask<bool> HandleFunctionCallAsync(
        string functionName,
        string functionCallId,
        string? functionCallArguments,
        RealtimeTurnContext context,
        CancellationToken cancellationToken)
    {
        if (!context.State.HandledFunctionCallIds.Add(functionCallId))
        {
            return true;
        }

        var outputPayload = "{\"ok\":false,\"error\":\"Unsupported tool call.\"}";
        CameraToolExecutionResult? cameraExecution = null;

        try
        {
            if (string.Equals(functionName, CameraTool.Name, StringComparison.OrdinalIgnoreCase))
            {
                cameraExecution = await cameraTool.ExecuteAsync(functionCallArguments ?? "{}", cancellationToken);
                outputPayload = cameraExecution.ToolOutputJson;

                context.Logger.LogInformation(
                    "Realtime camera tool executed for callId={CallId}, question=\"{Question}\", imageBytes={ImageBytes}.",
                    functionCallId,
                    cameraExecution.Question,
                    cameraExecution.Snapshot.ImageBytes.Length);
            }
            else
            {
                outputPayload = $"{{\"ok\":false,\"error\":\"Unsupported tool '{functionName}'.\"}}";
            }

            context.DisableMicSendAndTransitionToThinking("tool call execution");
            context.State.PendingToolContinuation = true;

            await context.RealtimeSession.AddItemAsync(
                ConversationItem.CreateFunctionCallOutput(functionCallId, outputPayload),
                cancellationToken);

            if (cameraExecution is not null)
            {
                await context.RealtimeSession.SendCommandAsync(
                    cameraTool.BuildRealtimeImageMessageCommand(cameraExecution),
                    new RequestOptions
                    {
                        CancellationToken = cancellationToken
                    });
            }

            await context.RealtimeSession.StartResponseAsync(cancellationToken);
            context.State.ResponseDeadlineUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(context.ResponseTimeoutMs);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.Logger.LogError(
                ex,
                "Realtime tool execution failed for tool={ToolName}, callId={CallId}.",
                functionName,
                functionCallId);
            context.CompleteFailure($"Realtime tool execution failed: {ex.Message}");
            return true;
        }
    }
}
