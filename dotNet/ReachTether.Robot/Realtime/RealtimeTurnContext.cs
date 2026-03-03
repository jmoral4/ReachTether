using ReachTether.Audio.Alsa;
using Microsoft.Extensions.Logging;
using ReachTether.Audio;

internal sealed class RealtimeTurnContext
{
    private readonly Func<string, bool> shutdownIntentDetector;

    public RealtimeTurnContext(
        RealtimeTurnState state,
        LocalAudioSession audioSession,
        IMotionOrchestrator motionOrchestrator,
        IInteractionStateMachine stateMachine,
        ILogger<RealtimeInteractionOrchestrator> logger,
        AudioFormat outputFormat,
        int outputSampleRateHz,
        int responseTimeoutMs,
        Func<string, bool> shutdownIntentDetector)
    {
        State = state;
        AudioSession = audioSession;
        MotionOrchestrator = motionOrchestrator;
        StateMachine = stateMachine;
        Logger = logger;
        OutputFormat = outputFormat;
        OutputSampleRateHz = outputSampleRateHz;
        ResponseTimeoutMs = responseTimeoutMs;
        this.shutdownIntentDetector = shutdownIntentDetector;
    }

    public RealtimeTurnState State { get; }
    public LocalAudioSession AudioSession { get; }
    public IMotionOrchestrator MotionOrchestrator { get; }
    public IInteractionStateMachine StateMachine { get; }
    public ILogger<RealtimeInteractionOrchestrator> Logger { get; }
    public AudioFormat OutputFormat { get; }
    public int OutputSampleRateHz { get; }
    public int ResponseTimeoutMs { get; }
    public bool IsCompleted => State.IsCompleted;
    public RealtimeTurnResult CompletedResult => State.CompletedResult
        ?? throw new InvalidOperationException("Turn result was not completed.");

    public bool IsShutdownIntent(string input) => shutdownIntentDetector(input);

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
