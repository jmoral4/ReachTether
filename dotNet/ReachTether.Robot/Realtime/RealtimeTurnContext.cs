using ReachTether.Audio.Alsa;
using Microsoft.Extensions.Logging;
using ReachTether.Audio;
using OpenAI.RealtimeConversation;

internal sealed class RealtimeTurnContext
{
    private readonly Func<string, bool> shutdownIntentDetector;

    public RealtimeTurnContext(
        RealtimeTurnState state,
        RealtimeConversationSession realtimeSession,
        LocalAudioSession audioSession,
        IMotionOrchestrator motionOrchestrator,
        IInteractionStateMachine stateMachine,
        ILogger<RealtimeInteractionOrchestrator> logger,
        AudioFormat outputFormat,
        int outputSampleRateHz,
        int responseTimeoutMs,
        int speechStopMicDisableGraceMs,
        bool requireTranscriptBeforeAssistantAudio,
        ISet<string> benignRealtimeErrorCodes,
        Func<string, bool> shutdownIntentDetector)
    {
        State = state;
        RealtimeSession = realtimeSession;
        AudioSession = audioSession;
        MotionOrchestrator = motionOrchestrator;
        StateMachine = stateMachine;
        Logger = logger;
        OutputFormat = outputFormat;
        OutputSampleRateHz = outputSampleRateHz;
        ResponseTimeoutMs = responseTimeoutMs;
        SpeechStopMicDisableGraceMs = speechStopMicDisableGraceMs;
        RequireTranscriptBeforeAssistantAudio = requireTranscriptBeforeAssistantAudio;
        BenignRealtimeErrorCodes = benignRealtimeErrorCodes;
        this.shutdownIntentDetector = shutdownIntentDetector;
    }

    public RealtimeTurnState State { get; }
    public RealtimeConversationSession RealtimeSession { get; }
    public LocalAudioSession AudioSession { get; }
    public IMotionOrchestrator MotionOrchestrator { get; }
    public IInteractionStateMachine StateMachine { get; }
    public ILogger<RealtimeInteractionOrchestrator> Logger { get; }
    public AudioFormat OutputFormat { get; }
    public int OutputSampleRateHz { get; }
    public int ResponseTimeoutMs { get; }
    public int SpeechStopMicDisableGraceMs { get; }
    public bool RequireTranscriptBeforeAssistantAudio { get; }
    public ISet<string> BenignRealtimeErrorCodes { get; }
    public bool IsCompleted => State.IsCompleted;
    public RealtimeTurnResult CompletedResult => State.CompletedResult
        ?? throw new InvalidOperationException("Turn result was not completed.");

    public bool IsShutdownIntent(string input) => shutdownIntentDetector(input);

    public void ScheduleMicDisableGraceWindow(DateTime utcNow)
    {
        if (SpeechStopMicDisableGraceMs <= 0)
        {
            State.PendingMicDisableDeadlineUtc = utcNow;
            return;
        }

        State.PendingMicDisableDeadlineUtc = utcNow + TimeSpan.FromMilliseconds(SpeechStopMicDisableGraceMs);
    }

    public void CancelPendingMicDisable()
    {
        State.PendingMicDisableDeadlineUtc = null;
    }

    public void DisableMicSendAndTransitionToThinking(string reason)
    {
        State.PendingMicDisableDeadlineUtc = null;

        if (Interlocked.Exchange(ref State.SendAudioEnabled, 0) == 0)
        {
            return;
        }

        // If assistant streaming already started, keep Speaking state stable.
        if (State.ResponseStarted && State.StreamOpen)
        {
            return;
        }

        Console.WriteLine("Reachy is thinking...");
        StateMachine.TransitionTo(InteractionState.Thinking, reason);
    }

    public void CompleteFailure(string reason)
    {
        State.Complete(new RealtimeTurnResult(
            State.UserTranscript,
            State.AssistantText.ToString(),
            State.StreamedAudioPlayback,
            reason));
    }

    public void CompleteSuccess()
    {
        State.Complete(new RealtimeTurnResult(
            State.UserTranscript,
            State.AssistantText.ToString().Trim(),
            State.StreamedAudioPlayback,
            null));
    }
}
