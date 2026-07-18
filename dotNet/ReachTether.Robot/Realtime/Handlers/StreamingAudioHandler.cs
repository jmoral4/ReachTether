internal sealed class StreamingAudioHandler : IRealtimeEventHandler
{
    public int Order => 300;

    public ValueTask<bool> HandleAsync(RealtimeServerEvent update, RealtimeTurnContext context, CancellationToken ct)
    {
        switch (update)
        {
            case RealtimeOutputAudioDeltaEvent delta:
                if (!MatchesActiveResponse(delta.ResponseId, context))
                {
                    return ValueTask.FromResult(true);
                }

                if (delta.AudioBytes.Length > 0
                    && !context.State.DropActiveResponseAudio
                    && !context.State.SuppressResponseForShutdownIntent
                    && (!context.RequireTranscriptBeforeAssistantAudio
                        || context.HasActiveInputTranscript))
                {
                    context.State.ActiveOutputItemId = delta.ItemId;
                    context.State.ActiveOutputContentIndex = delta.ContentIndex;
                    context.MotionOrchestrator.PushAssistantAudioPcm16(
                        delta.AudioBytes,
                        context.OutputSampleRateHz,
                        channels: 1);

                    if (!context.State.StreamOpen)
                    {
                        context.AudioOutput.Begin(context.OutputFormat);
                        context.State.StreamOpen = true;
                        context.State.StreamedAudioPlayback = true;
                        context.StateMachine.TransitionTo(InteractionState.Speaking, "realtime streaming audio");
                    }

                    context.AudioOutput.Write(delta.AudioBytes, ct);
                    context.State.StreamedAudioBytes += delta.AudioBytes.Length;
                }

                return ValueTask.FromResult(true);

            case RealtimeOutputAudioTranscriptDeltaEvent transcriptDelta:
                if (MatchesActiveResponse(transcriptDelta.ResponseId, context))
                {
                    context.State.AssistantText.Append(transcriptDelta.Delta);
                }
                return ValueTask.FromResult(true);

            case RealtimeOutputTextDeltaEvent textDelta:
                if (MatchesActiveResponse(textDelta.ResponseId, context))
                {
                    context.State.AssistantText.Append(textDelta.Delta);
                }
                return ValueTask.FromResult(true);

            case RealtimeOutputAudioTranscriptDoneEvent transcriptDone:
                if (MatchesActiveResponse(transcriptDone.ResponseId, context)
                    && context.State.AssistantText.Length == 0)
                {
                    context.State.AssistantText.Append(transcriptDone.Transcript);
                }
                return ValueTask.FromResult(true);

            case RealtimeOutputTextDoneEvent textDone:
                if (MatchesActiveResponse(textDone.ResponseId, context)
                    && context.State.AssistantText.Length == 0)
                {
                    context.State.AssistantText.Append(textDone.Text);
                }
                return ValueTask.FromResult(true);

            default:
                return ValueTask.FromResult(false);
        }
    }

    private static bool MatchesActiveResponse(string? responseId, RealtimeTurnContext context)
        => !string.IsNullOrWhiteSpace(context.State.ActiveResponseId)
            && !string.IsNullOrWhiteSpace(responseId)
            && !context.State.IgnoredResponseIds.Contains(responseId)
            && string.Equals(responseId, context.State.ActiveResponseId, StringComparison.Ordinal);
}
