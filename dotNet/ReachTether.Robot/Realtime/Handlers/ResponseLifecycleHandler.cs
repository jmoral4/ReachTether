using OpenAI.RealtimeConversation;
using Microsoft.Extensions.Logging;

internal sealed class ResponseLifecycleHandler : IRealtimeEventHandler
{
    public int Order => 400;

    public ValueTask<bool> HandleAsync(ConversationUpdate update, RealtimeTurnContext context, CancellationToken ct)
    {
        switch (update)
        {
            case ConversationResponseStartedUpdate started:
                context.State.ActiveResponseId = started.ResponseId;
                context.State.ResponseStarted = true;
                context.State.DropActiveResponseAudio = context.State.SuppressResponseForShutdownIntent;
                context.MotionOrchestrator.ResetTalkingGesture();
                context.State.ResponseDeadlineUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(context.ResponseTimeoutMs);
                return ValueTask.FromResult(true);

            case ConversationErrorUpdate errorUpdate:
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

            case ConversationResponseFinishedUpdate finished:
                if (!string.IsNullOrWhiteSpace(context.State.ActiveResponseId)
                    && !string.Equals(finished.ResponseId, context.State.ActiveResponseId, StringComparison.Ordinal))
                {
                    return ValueTask.FromResult(true);
                }

                if (context.State.StreamOpen)
                {
                    context.AudioSession.CompletePlaybackStream();
                    context.State.StreamFinalized = true;
                }

                if (string.IsNullOrWhiteSpace(context.State.UserTranscript))
                {
                    var durationMs = context.State.SpeechStartTime.HasValue && context.State.SpeechEndTime.HasValue
                        ? Math.Max(0, (context.State.SpeechEndTime.Value - context.State.SpeechStartTime.Value).TotalMilliseconds)
                        : 0;
                    var reason = string.IsNullOrWhiteSpace(context.State.TranscriptionFailureReason)
                        ? $"No input transcript produced (speechDurationMs={durationMs:F0})."
                        : $"Input transcription failed: {context.State.TranscriptionFailureReason} (speechDurationMs={durationMs:F0}).";
                    context.CompleteFailure(reason);
                    return ValueTask.FromResult(true);
                }

                context.CompleteSuccess();
                return ValueTask.FromResult(true);

            default:
                return ValueTask.FromResult(false);
        }
    }
}
