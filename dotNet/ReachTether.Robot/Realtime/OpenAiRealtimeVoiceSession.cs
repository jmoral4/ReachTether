#pragma warning disable OPENAI002

using System.Runtime.CompilerServices;
using System.ClientModel.Primitives;
using OpenAI.Realtime;

internal sealed class OpenAiRealtimeVoiceSessionFactory : IRealtimeVoiceSessionFactory
{
    private readonly RealtimeClient client;
    private readonly string model;

    public OpenAiRealtimeVoiceSessionFactory(string apiKey, string model)
    {
        client = new RealtimeClient(apiKey);
        this.model = model;
    }

    public async Task<IRealtimeVoiceSession> ConnectAsync(CancellationToken cancellationToken)
    {
        var session = await client.StartConversationSessionAsync(
            model,
            new RealtimeSessionClientOptions(),
            cancellationToken);
        return new OpenAiRealtimeVoiceSession(session);
    }
}

internal sealed class OpenAiRealtimeVoiceSession(RealtimeSessionClient session) : IRealtimeVoiceSession
{
    public async Task ConfigureAsync(
        RealtimeSessionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (configuration.InputSampleRateHz != 24000)
        {
            throw new InvalidOperationException(
                $"The GA Realtime PCM input format requires 24000 Hz audio; configured value was {configuration.InputSampleRateHz} Hz.");
        }
        if (configuration.OutputSampleRateHz != 24000)
        {
            throw new InvalidOperationException(
                $"The GA Realtime PCM output format is 24000 Hz; configured value was {configuration.OutputSampleRateHz} Hz.");
        }

        await session.ConfigureConversationSessionAsync(
            BuildSessionOptions(configuration),
            cancellationToken);
    }

    internal static RealtimeConversationSessionOptions BuildSessionOptions(
        RealtimeSessionConfiguration configuration)
    {
        var options = new RealtimeConversationSessionOptions
        {
            Model = configuration.Model,
            Instructions = configuration.Instructions,
            AudioOptions = new RealtimeConversationSessionAudioOptions
            {
                InputAudioOptions = new RealtimeConversationSessionInputAudioOptions
                {
                    AudioFormat = new RealtimePcmAudioFormat(),
                    AudioTranscriptionOptions = new RealtimeAudioTranscriptionOptions
                    {
                        Model = configuration.TranscriptionModel,
                        Language = configuration.TranscriptionLanguage
                    },
                    TurnDetection = new RealtimeServerVadTurnDetection
                    {
                        CreateResponseEnabled = true,
                        InterruptResponseEnabled = true
                    }
                },
                OutputAudioOptions = new RealtimeConversationSessionOutputAudioOptions
                {
                    AudioFormat = new RealtimePcmAudioFormat(),
                    Voice = new RealtimeVoice(configuration.Voice)
                }
            },
            ToolChoice = new RealtimeToolChoice(
                configuration.Tools.Count > 0
                    ? RealtimeDefaultToolChoice.Auto
                    : RealtimeDefaultToolChoice.None)
        };
        options.OutputModalities.Add(RealtimeOutputModality.Audio);

        foreach (var tool in configuration.Tools)
        {
            options.Tools.Add(new RealtimeFunctionTool(tool.Name)
            {
                FunctionDescription = tool.Description,
                FunctionParameters = tool.ParametersSchema
            });
        }

        return options;
    }

    public Task SendInputAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken cancellationToken)
        => session.SendInputAudioAsync(BinaryData.FromBytes(pcm16Audio), cancellationToken);

    public Task AddFunctionCallOutputAsync(
        string callId,
        string outputJson,
        CancellationToken cancellationToken)
        => session.AddItemAsync(
            RealtimeItem.CreateFunctionCallOutputItem(callId, outputJson),
            cancellationToken);

    public Task AddUserMessageAsync(RealtimeInputMessage message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message.ImageDataUrl))
        {
            return session.AddItemAsync(BuildUserMessageItem(message), cancellationToken);
        }

        return session.SendCommandAsync(
            BuildUserMessageCommand(message),
            new RequestOptions { CancellationToken = cancellationToken });
    }

    public Task StartResponseAsync(CancellationToken cancellationToken)
        => session.StartResponseAsync(cancellationToken);

    public Task CancelResponseAsync(string? responseId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(responseId))
        {
            return session.CancelResponseAsync(cancellationToken);
        }

        return session.SendCommandAsync(BuildCancelCommand(responseId), cancellationToken);
    }

    public Task TruncateAudioAsync(
        string itemId,
        int contentIndex,
        int audioEndMilliseconds,
        CancellationToken cancellationToken)
        => session.SendCommandAsync(
            BuildTruncationCommand(itemId, contentIndex, audioEndMilliseconds),
            cancellationToken);

    internal static RealtimeItem BuildUserMessageItem(RealtimeInputMessage message)
        => RealtimeItem.CreateUserMessageItem(
            [new RealtimeInputTextMessageContentPart(message.Text)]);

    internal static BinaryData BuildUserMessageCommand(RealtimeInputMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.ImageDataUrl))
        {
            throw new ArgumentException("An image data URL is required.", nameof(message));
        }

        return BinaryData.FromObjectAsJson(new
        {
            event_id = NewEventId(),
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "user",
                content = new object[]
                {
                    new { type = "input_text", text = message.Text },
                    new { type = "input_image", image_url = message.ImageDataUrl }
                }
            }
        });
    }

    internal static RealtimeClientCommandResponseCancel BuildCancelCommand(string responseId)
        => new()
        {
            EventId = NewEventId(),
            ResponseId = responseId
        };

    internal static RealtimeClientCommandConversationItemTruncate BuildTruncationCommand(
        string itemId,
        int contentIndex,
        int audioEndMilliseconds)
        => new(
            itemId,
            Math.Max(0, contentIndex),
            TimeSpan.FromMilliseconds(Math.Max(0, audioEndMilliseconds)))
        {
            EventId = NewEventId()
        };

    public async IAsyncEnumerable<RealtimeServerEvent> ReceiveEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var update in session.ReceiveUpdatesAsync(cancellationToken))
        {
            var mapped = MapServerUpdate(update);
            if (mapped is not null)
            {
                yield return mapped;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        session.Dispose();
        return ValueTask.CompletedTask;
    }

    internal static RealtimeServerEvent? MapServerUpdate(RealtimeServerUpdate update)
    {
        return update switch
        {
            RealtimeServerUpdateInputAudioBufferSpeechStarted speechStarted
                => new RealtimeSpeechStartedEvent(
                    speechStarted.EventId,
                    speechStarted.ItemId,
                    speechStarted.AudioStartTime),
            RealtimeServerUpdateInputAudioBufferSpeechStopped speechStopped
                => new RealtimeSpeechStoppedEvent(
                    speechStopped.EventId,
                    speechStopped.ItemId,
                    speechStopped.AudioEndTime),
            RealtimeServerUpdateConversationItemInputAudioTranscriptionCompleted transcription
                => new RealtimeInputTranscriptionCompletedEvent(
                    transcription.EventId,
                    transcription.ItemId,
                    transcription.Transcript),
            RealtimeServerUpdateConversationItemInputAudioTranscriptionFailed transcriptionFailed
                => new RealtimeInputTranscriptionFailedEvent(
                    transcriptionFailed.EventId,
                    transcriptionFailed.ItemId,
                    transcriptionFailed.Error.Code,
                    transcriptionFailed.Error.Message,
                    transcriptionFailed.Error.ParameterName),
            RealtimeServerUpdateResponseCreated responseStarted
                => new RealtimeResponseStartedEvent(
                    responseStarted.EventId,
                    responseStarted.Response.Id),
            RealtimeServerUpdateResponseDone responseFinished
                => new RealtimeResponseFinishedEvent(
                    responseFinished.EventId,
                    responseFinished.Response.Id,
                    responseFinished.Response.Status?.ToString()),
            RealtimeServerUpdateResponseOutputAudioDelta audioDelta
                => new RealtimeOutputAudioDeltaEvent(
                    audioDelta.EventId,
                    audioDelta.ResponseId,
                    audioDelta.ItemId,
                    audioDelta.ContentIndex,
                    audioDelta.Delta.ToArray()),
            RealtimeServerUpdateResponseOutputAudioTranscriptDelta transcriptDelta
                => new RealtimeOutputAudioTranscriptDeltaEvent(
                    transcriptDelta.EventId,
                    transcriptDelta.ResponseId,
                    transcriptDelta.Delta),
            RealtimeServerUpdateResponseOutputAudioTranscriptDone transcriptDone
                => new RealtimeOutputAudioTranscriptDoneEvent(
                    transcriptDone.EventId,
                    transcriptDone.ResponseId,
                    transcriptDone.Transcript),
            RealtimeServerUpdateResponseOutputTextDelta textDelta
                => new RealtimeOutputTextDeltaEvent(
                    textDelta.EventId,
                    textDelta.ResponseId,
                    textDelta.Delta),
            RealtimeServerUpdateResponseOutputTextDone textDone
                => new RealtimeOutputTextDoneEvent(
                    textDone.EventId,
                    textDone.ResponseId,
                    textDone.Text),
            RealtimeServerUpdateResponseFunctionCallArgumentsDone functionCall
                => new RealtimeFunctionCallEvent(
                    functionCall.EventId,
                    functionCall.ResponseId,
                    functionCall.ItemId,
                    null,
                    functionCall.FunctionName,
                    functionCall.CallId,
                    functionCall.FunctionArguments.ToString()),
            RealtimeServerUpdateResponseOutputItemDone { Item: RealtimeFunctionCallItem functionItem } itemDone
                => new RealtimeFunctionCallEvent(
                    itemDone.EventId,
                    itemDone.ResponseId,
                    functionItem.Id,
                    functionItem.Status?.ToString(),
                    functionItem.FunctionName,
                    functionItem.CallId,
                    functionItem.FunctionArguments.ToString()),
            RealtimeServerUpdateError error
                => new RealtimeErrorEvent(
                    error.EventId,
                    error.Error.Code,
                    error.Error.Message,
                    error.Error.ParameterName),
            _ => null
        };
    }

    private static string NewEventId() => $"event_{Guid.NewGuid():N}";
}

#pragma warning restore OPENAI002
