using Microsoft.Extensions.Logging;

internal sealed class TranscriptionHandler : IRealtimeEventHandler
{
    public int Order => 200;

    public async ValueTask<bool> HandleAsync(
        RealtimeServerEvent update,
        RealtimeTurnContext context,
        CancellationToken ct)
    {
        switch (update)
        {
            case RealtimeInputTranscriptionCompletedEvent inputUpdate:
                if (!MatchesActiveInputItem(inputUpdate.ItemId, context))
                {
                    return true;
                }

                context.State.UserTranscript = inputUpdate.Transcript.Trim();
                context.State.UserTranscriptItemId = inputUpdate.ItemId;
                context.State.TranscriptionFailureReason = null;

                if (context.State.ResponseFinishedPendingTranscript)
                {
                    context.CompleteSuccess();
                }

                if (!string.IsNullOrWhiteSpace(context.State.UserTranscript)
                    && context.IsShutdownIntent(context.State.UserTranscript))
                {
                    context.State.SuppressResponseForShutdownIntent = true;
                    context.State.DropActiveResponseAudio = true;

                    if (context.State.StreamOpen && !context.State.StreamFinalized)
                    {
                        context.AudioOutput.Cancel();
                        context.State.StreamOpen = false;
                        context.State.StreamFinalized = true;
                        context.StateMachine.TransitionTo(InteractionState.Thinking, "shutdown intent detected");
                    }

                    if (context.State.ResponseStarted)
                    {
                        try
                        {
                            await context.RealtimeSession.CancelResponseAsync(
                                context.State.ActiveResponseId,
                                ct);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            context.Logger.LogDebug(ex, "Realtime response was already stopped for shutdown intent.");
                        }
                    }
                }

                return true;

            case RealtimeInputTranscriptionFailedEvent inputFailure:
                if (!MatchesActiveInputItem(inputFailure.ItemId, context))
                {
                    return true;
                }

                context.State.TranscriptionFailureReason =
                    $"{inputFailure.ErrorCode}: {inputFailure.ErrorMessage}";
                context.Logger.LogWarning(
                    "Realtime input transcription failed: code={Code}, message={Message}, param={Param}",
                    inputFailure.ErrorCode,
                    inputFailure.ErrorMessage,
                    inputFailure.ErrorParameterName);
                if (context.State.ResponseFinishedPendingTranscript)
                {
                    context.CompleteFailure(context.BuildMissingTranscriptFailureReason());
                }
                return true;

            default:
                return false;
        }
    }

    private static bool MatchesActiveInputItem(string itemId, RealtimeTurnContext context)
        => !string.IsNullOrWhiteSpace(context.State.ActiveInputItemId)
            && !string.IsNullOrWhiteSpace(itemId)
            && string.Equals(itemId, context.State.ActiveInputItemId, StringComparison.Ordinal);
}
