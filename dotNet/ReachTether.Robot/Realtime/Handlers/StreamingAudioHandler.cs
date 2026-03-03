using OpenAI.RealtimeConversation;

internal sealed class StreamingAudioHandler : IRealtimeEventHandler
{
    public int Order => 300;

    public ValueTask<bool> HandleAsync(ConversationUpdate update, RealtimeTurnContext context, CancellationToken ct)
    {
        switch (update)
        {
            case ConversationItemStreamingPartDeltaUpdate delta:
                if (!string.IsNullOrWhiteSpace(context.State.ActiveResponseId)
                    && !string.Equals(delta.ResponseId, context.State.ActiveResponseId, StringComparison.Ordinal))
                {
                    return ValueTask.FromResult(true);
                }

                if (delta.AudioBytes is { } audioData)
                {
                    var audioChunk = audioData.ToArray();
                    if (audioChunk.Length > 0
                        && !context.State.DropActiveResponseAudio
                        && !context.State.SuppressResponseForShutdownIntent
                        && !string.IsNullOrWhiteSpace(context.State.UserTranscript))
                    {
                        context.MotionOrchestrator.PushAssistantAudioPcm16(
                            audioChunk,
                            context.OutputSampleRateHz,
                            channels: 1);

                        if (!context.State.StreamOpen)
                        {
                            context.AudioSession.BeginPlaybackStream(context.OutputFormat);
                            context.State.StreamOpen = true;
                            context.State.StreamedAudioPlayback = true;
                            context.StateMachine.TransitionTo(InteractionState.Speaking, "realtime streaming audio");
                        }

                        context.AudioSession.WritePlaybackPcm16Chunk(audioChunk, ct);
                    }
                }

                if (!string.IsNullOrWhiteSpace(delta.Text))
                {
                    context.State.AssistantText.Append(delta.Text);
                }
                else if (!string.IsNullOrWhiteSpace(delta.AudioTranscript))
                {
                    context.State.AssistantText.Append(delta.AudioTranscript);
                }

                return ValueTask.FromResult(true);

            case ConversationItemStreamingPartFinishedUpdate finishedPart:
                if (!string.IsNullOrWhiteSpace(finishedPart.Text) && context.State.AssistantText.Length == 0)
                {
                    context.State.AssistantText.Append(finishedPart.Text);
                }
                else if (!string.IsNullOrWhiteSpace(finishedPart.AudioTranscript) && context.State.AssistantText.Length == 0)
                {
                    context.State.AssistantText.Append(finishedPart.AudioTranscript);
                }

                return ValueTask.FromResult(true);

            default:
                return ValueTask.FromResult(false);
        }
    }
}
