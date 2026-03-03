using OpenAI.RealtimeConversation;
using Microsoft.Extensions.Logging;

internal sealed class TranscriptionHandler : IRealtimeEventHandler
{
    public int Order => 200;

    public ValueTask<bool> HandleAsync(ConversationUpdate update, RealtimeTurnContext context, CancellationToken ct)
    {
        switch (update)
        {
            case ConversationInputTranscriptionFinishedUpdate inputUpdate:
                context.State.UserTranscript = inputUpdate.Transcript?.Trim();
                context.State.TranscriptionFailureReason = null;

                if (!string.IsNullOrWhiteSpace(context.State.UserTranscript)
                    && context.IsShutdownIntent(context.State.UserTranscript))
                {
                    context.State.SuppressResponseForShutdownIntent = true;
                    context.State.DropActiveResponseAudio = true;

                    if (context.State.StreamOpen && !context.State.StreamFinalized)
                    {
                        context.AudioSession.CancelPlaybackStream();
                        context.State.StreamOpen = false;
                        context.State.StreamFinalized = true;
                        context.StateMachine.TransitionTo(InteractionState.Thinking, "shutdown intent detected");
                    }
                }

                return ValueTask.FromResult(true);

            case ConversationInputTranscriptionFailedUpdate inputFailure:
                context.State.TranscriptionFailureReason =
                    $"{inputFailure.ErrorCode}: {inputFailure.ErrorMessage}";
                context.Logger.LogWarning(
                    "Realtime input transcription failed: code={Code}, message={Message}, param={Param}",
                    inputFailure.ErrorCode,
                    inputFailure.ErrorMessage,
                    inputFailure.ErrorParameterName);
                return ValueTask.FromResult(true);

            default:
                return ValueTask.FromResult(false);
        }
    }
}
