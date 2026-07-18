using Microsoft.Extensions.Logging;

internal sealed class SpeechBoundaryHandler : IRealtimeEventHandler
{
    public int Order => 100;

    public async ValueTask<bool> HandleAsync(RealtimeServerEvent update, RealtimeTurnContext context, CancellationToken ct)
    {
        switch (update)
        {
            case RealtimeSpeechStartedEvent startedSpeech:
                context.State.SpeechStarted = true;
                context.State.SpeechStopped = false;
                context.State.SpeechStartTime = startedSpeech.AudioStartTime;
                context.State.ActiveInputItemId = startedSpeech.ItemId;
                context.State.UserTranscriptItemId = null;
                context.State.UserTranscript = null;
                context.State.TranscriptionFailureReason = null;
                context.State.ResponseFinishedPendingTranscript = false;
                context.CancelPendingMicDisable();
                Interlocked.Exchange(ref context.State.SendAudioEnabled, 1);

                if (context.State.ResponseStarted)
                {
                    if (!string.IsNullOrWhiteSpace(context.State.ActiveResponseId))
                    {
                        context.State.IgnoredResponseIds.Add(context.State.ActiveResponseId);
                        context.State.ClearAssistantTextOnNextResponse = true;
                    }

                    if (context.State.StreamOpen && !context.State.StreamFinalized)
                    {
                        context.AudioOutput.Cancel();
                        context.State.StreamOpen = false;
                        context.State.StreamFinalized = true;
                    }

                    context.State.DropActiveResponseAudio = true;
                    context.MotionOrchestrator.ResetTalkingGesture();
                    context.StateMachine.TransitionTo(InteractionState.Listening, "barge-in");

                    if (!string.IsNullOrWhiteSpace(context.State.ActiveOutputItemId))
                    {
                        var playedMilliseconds = (int)Math.Clamp(
                            context.State.StreamedAudioBytes * 1000L / (context.OutputSampleRateHz * 2L),
                            0,
                            int.MaxValue);
                        try
                        {
                            await context.RealtimeSession.TruncateAudioAsync(
                                context.State.ActiveOutputItemId,
                                context.State.ActiveOutputContentIndex,
                                playedMilliseconds,
                                ct);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            context.Logger.LogWarning(ex, "Failed to truncate interrupted realtime audio item.");
                        }
                    }

                    context.State.ActiveResponseId = null;
                    context.State.ResponseStarted = false;
                    context.State.ActiveOutputItemId = null;
                    context.State.StreamedAudioBytes = 0;
                }

                return true;

            case RealtimeSpeechStoppedEvent finishedSpeech:
                if (!string.IsNullOrWhiteSpace(context.State.ActiveInputItemId)
                    && string.Equals(
                        finishedSpeech.ItemId,
                        context.State.ActiveInputItemId,
                        StringComparison.Ordinal)
                    && !context.State.SpeechStopped)
                {
                    context.State.SpeechStopped = true;
                    context.State.SpeechEndTime = finishedSpeech.AudioEndTime;
                    context.ScheduleMicDisableGraceWindow(DateTime.UtcNow);

                    if (context.SpeechStopMicDisableGraceMs <= 0)
                    {
                        context.DisableMicSendAndTransitionToThinking("server vad speech stopped");
                    }
                }

                return true;

            default:
                return false;
        }
    }
}
