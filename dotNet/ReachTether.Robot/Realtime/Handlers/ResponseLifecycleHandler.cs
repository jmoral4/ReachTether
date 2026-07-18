using Microsoft.Extensions.Logging;

internal sealed class ResponseLifecycleHandler : IRealtimeEventHandler
{
    public int Order => 400;

    public ValueTask<bool> HandleAsync(
        RealtimeServerEvent update,
        RealtimeTurnContext context,
        CancellationToken ct)
    {
        switch (update)
        {
            case RealtimeResponseStartedEvent started:
                if (context.State.ClearAssistantTextOnNextResponse)
                {
                    context.State.AssistantText.Clear();
                    context.State.ClearAssistantTextOnNextResponse = false;
                }

                context.State.ActiveResponseId = started.ResponseId;
                context.State.IgnoredResponseIds.Remove(started.ResponseId);
                context.State.ActiveOutputItemId = null;
                context.State.ActiveOutputContentIndex = 0;
                context.State.StreamedAudioBytes = 0;
                context.State.StreamFinalized = false;
                context.State.ResponseStarted = true;
                context.State.ResponseFinishedPendingTranscript = false;
                context.State.PendingToolContinuation = false;
                context.State.DropActiveResponseAudio = context.State.SuppressResponseForShutdownIntent;
                context.MotionOrchestrator.ResetTalkingGesture();
                context.State.ResponseDeadlineUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(context.ResponseTimeoutMs);
                return ValueTask.FromResult(true);

            case RealtimeErrorEvent errorUpdate:
                var errorCode = errorUpdate.ErrorCode?.Trim();
                if (!string.IsNullOrWhiteSpace(errorCode)
                    && context.BenignRealtimeErrorCodes.Contains(errorCode))
                {
                    context.Logger.LogDebug(
                        "Ignoring benign realtime error: code={Code}, message={Message}",
                        errorCode,
                        errorUpdate.Message);
                    return ValueTask.FromResult(true);
                }

                context.CompleteFailure($"Realtime API error: {errorUpdate.ErrorCode}: {errorUpdate.Message}");
                return ValueTask.FromResult(true);

            case RealtimeResponseFinishedEvent finished:
                if (context.State.IgnoredResponseIds.Remove(finished.ResponseId))
                {
                    return ValueTask.FromResult(true);
                }

                if (string.IsNullOrWhiteSpace(context.State.ActiveResponseId)
                    || !string.Equals(finished.ResponseId, context.State.ActiveResponseId, StringComparison.Ordinal))
                {
                    return ValueTask.FromResult(true);
                }

                if (context.State.PendingToolContinuation)
                {
                    return ValueTask.FromResult(true);
                }

                if (context.State.StreamOpen)
                {
                    context.AudioOutput.Complete();
                    context.State.StreamFinalized = true;
                }

                if (string.Equals(finished.Status, "cancelled", StringComparison.OrdinalIgnoreCase)
                    && !context.State.SuppressResponseForShutdownIntent)
                {
                    context.CompleteFailure("Realtime response was cancelled before completion.");
                    return ValueTask.FromResult(true);
                }

                if (!string.Equals(finished.Status, "completed", StringComparison.OrdinalIgnoreCase)
                    && !(context.State.SuppressResponseForShutdownIntent
                        && string.Equals(finished.Status, "cancelled", StringComparison.OrdinalIgnoreCase)))
                {
                    context.CompleteFailure($"Realtime response ended with status '{finished.Status}'.");
                    return ValueTask.FromResult(true);
                }

                if (!context.HasActiveInputTranscript)
                {
                    if (!string.IsNullOrWhiteSpace(context.State.TranscriptionFailureReason))
                    {
                        context.CompleteFailure(context.BuildMissingTranscriptFailureReason());
                    }
                    else
                    {
                        context.DeferCompletionUntilTranscript();
                    }
                    return ValueTask.FromResult(true);
                }

                context.CompleteSuccess();
                return ValueTask.FromResult(true);

            default:
                return ValueTask.FromResult(false);
        }
    }
}
