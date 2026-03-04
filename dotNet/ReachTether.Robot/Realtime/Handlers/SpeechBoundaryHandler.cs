using OpenAI.RealtimeConversation;

internal sealed class SpeechBoundaryHandler : IRealtimeEventHandler
{
    public int Order => 100;

    public ValueTask<bool> HandleAsync(ConversationUpdate update, RealtimeTurnContext context, CancellationToken ct)
    {
        switch (update)
        {
            case ConversationInputSpeechStartedUpdate startedSpeech:
                context.State.SpeechStarted = true;
                context.State.SpeechStopped = false;
                context.State.SpeechStartTime = startedSpeech.AudioStartTime;
                context.CancelPendingMicDisable();
                Interlocked.Exchange(ref context.State.SendAudioEnabled, 1);

                if (context.State.ResponseStarted && context.State.StreamOpen && !context.State.StreamFinalized)
                {
                    context.AudioSession.CancelPlaybackStream();
                    context.State.StreamOpen = false;
                    context.State.StreamFinalized = true;
                    context.State.DropActiveResponseAudio = true;
                    context.MotionOrchestrator.ResetTalkingGesture();
                    context.StateMachine.TransitionTo(InteractionState.Listening, "barge-in");
                }

                return ValueTask.FromResult(true);

            case ConversationInputSpeechFinishedUpdate finishedSpeech:
                if (!context.State.SpeechStopped)
                {
                    context.State.SpeechStopped = true;
                    context.State.SpeechEndTime = finishedSpeech.AudioEndTime;
                    context.ScheduleMicDisableGraceWindow(DateTime.UtcNow);

                    if (context.SpeechStopMicDisableGraceMs <= 0)
                    {
                        context.DisableMicSendAndTransitionToThinking("server vad speech stopped");
                    }
                }

                return ValueTask.FromResult(true);

            default:
                return ValueTask.FromResult(false);
        }
    }
}
