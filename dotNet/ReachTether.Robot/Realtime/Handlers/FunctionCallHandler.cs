using Microsoft.Extensions.Logging;

internal sealed class FunctionCallHandler(IToolRouter toolRouter) : IRealtimeEventHandler
{
    public int Order => 250;

    public async ValueTask<bool> HandleAsync(
        RealtimeServerEvent update,
        RealtimeTurnContext context,
        CancellationToken ct)
    {
        if (update is RealtimeFunctionCallEvent functionCall)
        {
            CollectPendingCall(functionCall, context);
            return true;
        }

        if (update is not RealtimeResponseFinishedEvent responseFinished)
        {
            return false;
        }

        var callsForResponse = context.State.PendingFunctionCalls
            .Where(entry => string.Equals(
                entry.Key.ResponseId,
                responseFinished.ResponseId,
                StringComparison.Ordinal))
            .Select(entry => entry.Value)
            .ToArray();

        foreach (var call in callsForResponse)
        {
            context.State.PendingFunctionCalls.Remove((call.ResponseId, call.ItemId));
        }

        if (context.State.IgnoredResponseIds.Contains(responseFinished.ResponseId)
            || !string.Equals(responseFinished.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return callsForResponse.Length > 0;
        }

        var completedCalls = callsForResponse
            .Where(call => string.Equals(call.ItemStatus, "completed", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (completedCalls.Length == 0)
        {
            return callsForResponse.Length > 0;
        }

        var executedAny = false;
        foreach (var pendingCall in completedCalls)
        {
            if (!context.State.HandledFunctionCallIds.Add(pendingCall.FunctionCallId))
            {
                continue;
            }

            if (!await ExecuteToolCallAsync(pendingCall, context, ct))
            {
                return true;
            }

            executedAny = true;
        }

        if (executedAny)
        {
            context.DisableMicSendAndTransitionToThinking("tool call execution");
            context.State.PendingToolContinuation = true;
            await context.RealtimeSession.StartResponseAsync(ct);
            context.State.ResponseDeadlineUtc =
                DateTime.UtcNow + TimeSpan.FromMilliseconds(context.ResponseTimeoutMs);
        }

        return true;
    }

    private static void CollectPendingCall(
        RealtimeFunctionCallEvent functionCall,
        RealtimeTurnContext context)
    {
        if (string.IsNullOrWhiteSpace(functionCall.ResponseId)
            || string.IsNullOrWhiteSpace(functionCall.ItemId)
            || string.IsNullOrWhiteSpace(functionCall.FunctionName)
            || string.IsNullOrWhiteSpace(functionCall.FunctionCallId))
        {
            return;
        }

        var key = (functionCall.ResponseId, functionCall.ItemId);
        if (context.State.PendingFunctionCalls.TryGetValue(key, out var existing)
            && string.IsNullOrWhiteSpace(functionCall.ItemStatus)
            && !string.IsNullOrWhiteSpace(existing.ItemStatus))
        {
            functionCall = functionCall with { ItemStatus = existing.ItemStatus };
        }

        context.State.PendingFunctionCalls[key] = functionCall;
    }

    private async Task<bool> ExecuteToolCallAsync(
        RealtimeFunctionCallEvent functionCall,
        RealtimeTurnContext context,
        CancellationToken ct)
    {
        try
        {
            var startedAt = DateTimeOffset.UtcNow;
            var execution = await toolRouter.ExecuteAsync(
                new ToolExecutionRequest(
                    functionCall.FunctionCallId,
                    functionCall.FunctionName,
                    functionCall.FunctionCallArguments,
                    context.SessionId,
                    context.TurnId,
                    ToolInvocationSource.Realtime),
                ct);
            context.State.ToolCalls.Add(new PersistedToolCallDescriptor(
                functionCall.FunctionCallId,
                functionCall.FunctionName,
                functionCall.FunctionCallArguments,
                execution.OutputJson,
                execution.Succeeded ? "succeeded" : "failed",
                startedAt));
            context.State.Artifacts.AddRange(execution.Artifacts.Select(artifact =>
                ServerSessionCoordinator.ToPersistedArtifactDescriptor(
                    context.TurnId,
                    artifact,
                    functionCall.FunctionCallId)));

            await context.RealtimeSession.AddFunctionCallOutputAsync(
                functionCall.FunctionCallId,
                execution.OutputJson,
                ct);

            foreach (var input in execution.RealtimeInputs)
            {
                await context.RealtimeSession.AddUserMessageAsync(input, ct);
            }

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
                functionCall.FunctionName,
                functionCall.FunctionCallId);
            context.CompleteFailure($"Realtime tool execution failed: {ex.Message}");
            return false;
        }
    }
}
