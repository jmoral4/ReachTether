internal interface IRealtimeVoiceSessionFactory
{
    Task<IRealtimeVoiceSession> ConnectAsync(CancellationToken cancellationToken);
}

internal interface IRealtimeVoiceSession : IAsyncDisposable
{
    Task ConfigureAsync(RealtimeSessionConfiguration configuration, CancellationToken cancellationToken);
    Task SendInputAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken cancellationToken);
    Task AddFunctionCallOutputAsync(string callId, string outputJson, CancellationToken cancellationToken);
    Task AddUserMessageAsync(RealtimeInputMessage message, CancellationToken cancellationToken);
    Task StartResponseAsync(CancellationToken cancellationToken);
    Task CancelResponseAsync(string? responseId, CancellationToken cancellationToken);
    Task TruncateAudioAsync(string itemId, int contentIndex, int audioEndMilliseconds, CancellationToken cancellationToken);
    IAsyncEnumerable<RealtimeServerEvent> ReceiveEventsAsync(CancellationToken cancellationToken);
}

internal sealed record RealtimeSessionConfiguration(
    string Model,
    string Instructions,
    string Voice,
    string TranscriptionModel,
    string TranscriptionLanguage,
    int InputSampleRateHz,
    int OutputSampleRateHz,
    IReadOnlyList<RealtimeToolDefinition> Tools);

internal abstract record RealtimeServerEvent(string Type, string? EventId);

internal sealed record RealtimeSpeechStartedEvent(
    string? EventId,
    string? ItemId,
    TimeSpan AudioStartTime)
    : RealtimeServerEvent("input_audio_buffer.speech_started", EventId);

internal sealed record RealtimeSpeechStoppedEvent(
    string? EventId,
    string? ItemId,
    TimeSpan AudioEndTime)
    : RealtimeServerEvent("input_audio_buffer.speech_stopped", EventId);

internal sealed record RealtimeInputTranscriptionCompletedEvent(
    string? EventId,
    string ItemId,
    string Transcript)
    : RealtimeServerEvent("conversation.item.input_audio_transcription.completed", EventId);

internal sealed record RealtimeInputTranscriptionFailedEvent(
    string? EventId,
    string ItemId,
    string? ErrorCode,
    string? ErrorMessage,
    string? ErrorParameterName)
    : RealtimeServerEvent("conversation.item.input_audio_transcription.failed", EventId);

internal sealed record RealtimeResponseStartedEvent(
    string? EventId,
    string ResponseId)
    : RealtimeServerEvent("response.created", EventId);

internal sealed record RealtimeResponseFinishedEvent(
    string? EventId,
    string ResponseId,
    string? Status)
    : RealtimeServerEvent("response.done", EventId);

internal sealed record RealtimeOutputAudioDeltaEvent(
    string? EventId,
    string? ResponseId,
    string? ItemId,
    int ContentIndex,
    byte[] AudioBytes)
    : RealtimeServerEvent("response.output_audio.delta", EventId);

internal sealed record RealtimeOutputAudioTranscriptDeltaEvent(
    string? EventId,
    string? ResponseId,
    string Delta)
    : RealtimeServerEvent("response.output_audio_transcript.delta", EventId);

internal sealed record RealtimeOutputAudioTranscriptDoneEvent(
    string? EventId,
    string? ResponseId,
    string Transcript)
    : RealtimeServerEvent("response.output_audio_transcript.done", EventId);

internal sealed record RealtimeOutputTextDeltaEvent(
    string? EventId,
    string? ResponseId,
    string Delta)
    : RealtimeServerEvent("response.output_text.delta", EventId);

internal sealed record RealtimeOutputTextDoneEvent(
    string? EventId,
    string? ResponseId,
    string Text)
    : RealtimeServerEvent("response.output_text.done", EventId);

internal sealed record RealtimeFunctionCallEvent(
    string? EventId,
    string ResponseId,
    string ItemId,
    string? ItemStatus,
    string FunctionName,
    string FunctionCallId,
    string FunctionCallArguments)
    : RealtimeServerEvent("response.function_call_arguments.done", EventId);

internal sealed record RealtimeErrorEvent(
    string? EventId,
    string? ErrorCode,
    string Message,
    string? ErrorParameterName)
    : RealtimeServerEvent("error", EventId);
