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
                context.State.SpeechStartTime = startedSpeech.AudioStartTime;

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
                    Interlocked.Exchange(ref context.State.SendAudioEnabled, 0);
                    Console.WriteLine("Reachy is thinking...");
                    context.StateMachine.TransitionTo(InteractionState.Thinking, "server vad speech stopped");
                }

                return ValueTask.FromResult(true);

            default:
                return ValueTask.FromResult(false);
        }
    }
}
