#pragma warning disable OPENAI002

using System.Runtime.CompilerServices;
using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Realtime;
using ReachTether.Audio;
using Xunit;

public sealed class RealtimePipelineTests
{
    [Fact]
    public async Task SpeechStarted_CancelsPlaybackAndTruncatesInterruptedAudio()
    {
        var fixture = CreateFixture();
        fixture.State.ResponseStarted = true;
        fixture.State.StreamOpen = true;
        fixture.State.ActiveResponseId = "resp_1";
        fixture.State.ActiveOutputItemId = "item_1";
        fixture.State.ActiveOutputContentIndex = 2;
        fixture.State.StreamedAudioBytes = 48_000;

        var handled = await new SpeechBoundaryHandler().HandleAsync(
            new RealtimeSpeechStartedEvent("event_1", "user_item", TimeSpan.FromMilliseconds(125)),
            fixture.Context,
            CancellationToken.None);

        Assert.True(handled);
        Assert.True(fixture.Audio.Cancelled);
        var truncation = Assert.Single(fixture.Session.Truncations);
        Assert.Equal(("item_1", 2, 1000), truncation);
        Assert.Equal(InteractionState.Listening, fixture.StateMachine.Current);
        Assert.Null(fixture.State.ActiveResponseId);
        Assert.Contains("resp_1", fixture.State.IgnoredResponseIds);
    }

    [Fact]
    public async Task SpeechStopped_RecordsBoundaryAndTransitionsToThinking()
    {
        var fixture = CreateFixture(speechStopGraceMs: 0);

        var handled = await new SpeechBoundaryHandler().HandleAsync(
            new RealtimeSpeechStoppedEvent("event_2", "user_item", TimeSpan.FromMilliseconds(950)),
            fixture.Context,
            CancellationToken.None);

        Assert.True(handled);
        Assert.True(fixture.State.SpeechStopped);
        Assert.Equal(TimeSpan.FromMilliseconds(950), fixture.State.SpeechEndTime);
        Assert.Equal(InteractionState.Thinking, fixture.StateMachine.Current);
        Assert.Equal(0, Volatile.Read(ref fixture.State.SendAudioEnabled));
    }

    [Fact]
    public async Task StreamingAudio_WritesPcmAndAccumulatesTranscript()
    {
        var fixture = CreateFixture();
        var lifecycle = new ResponseLifecycleHandler();
        var streaming = new StreamingAudioHandler();
        await lifecycle.HandleAsync(
            new RealtimeResponseStartedEvent("event_3", "resp_1"),
            fixture.Context,
            CancellationToken.None);

        await streaming.HandleAsync(
            new RealtimeOutputAudioDeltaEvent("event_4", "resp_1", "item_1", 0, [1, 2, 3, 4]),
            fixture.Context,
            CancellationToken.None);
        await streaming.HandleAsync(
            new RealtimeOutputAudioTranscriptDeltaEvent("event_5", "resp_1", "hello"),
            fixture.Context,
            CancellationToken.None);

        Assert.True(fixture.Audio.Begun);
        Assert.Equal([1, 2, 3, 4], Assert.Single(fixture.Audio.Writes));
        Assert.Equal("hello", fixture.State.AssistantText.ToString());
        Assert.Equal(4, fixture.State.StreamedAudioBytes);
        Assert.Equal(InteractionState.Speaking, fixture.StateMachine.Current);
    }

    [Fact]
    public async Task ShutdownTranscription_CancelsActiveResponse()
    {
        var fixture = CreateFixture(shutdownIntentDetector: input => input == "goodbye");
        fixture.State.ResponseStarted = true;
        fixture.State.ActiveResponseId = "resp_1";
        fixture.State.ActiveInputItemId = "user_item";

        var handled = await new TranscriptionHandler().HandleAsync(
            new RealtimeInputTranscriptionCompletedEvent("event_6", "user_item", " goodbye "),
            fixture.Context,
            CancellationToken.None);

        Assert.True(handled);
        Assert.True(fixture.State.SuppressResponseForShutdownIntent);
        Assert.Equal("resp_1", Assert.Single(fixture.Session.CancelledResponseIds));
        Assert.Equal("goodbye", fixture.State.UserTranscript);
    }

    [Fact]
    public async Task FunctionCall_AddsOutputAndStartsContinuationResponse()
    {
        var fixture = CreateFixture();
        var router = new FakeToolRouter();

        var handler = new FunctionCallHandler(router);
        var handled = await handler.HandleAsync(
            new RealtimeFunctionCallEvent(
                "event_7",
                "resp_1",
                "item_1",
                null,
                "camera",
                "call_1",
                "{\"question\":\"what is here?\"}"),
            fixture.Context,
            CancellationToken.None);

        Assert.True(handled);
        Assert.Empty(fixture.Session.FunctionOutputs);

        await handler.HandleAsync(
            new RealtimeFunctionCallEvent(
                "event_8",
                "resp_1",
                "item_1",
                "completed",
                "camera",
                "call_1",
                "{\"question\":\"what is here?\"}"),
            fixture.Context,
            CancellationToken.None);
        Assert.Empty(fixture.Session.FunctionOutputs);

        await handler.HandleAsync(
            new RealtimeResponseFinishedEvent("event_9", "resp_1", "completed"),
            fixture.Context,
            CancellationToken.None);

        Assert.Equal(("call_1", "{\"ok\":true}"), Assert.Single(fixture.Session.FunctionOutputs));
        Assert.Equal("inspect the image", Assert.Single(fixture.Session.UserMessages).Text);
        Assert.Equal(1, fixture.Session.StartResponseCount);
        Assert.True(fixture.State.PendingToolContinuation);
        Assert.Single(fixture.State.ToolCalls);
    }

    [Fact]
    public async Task CompletedResponse_ProducesSuccessfulTurnResult()
    {
        var fixture = CreateFixture();
        fixture.State.UserTranscript = "hello";
        fixture.State.ActiveInputItemId = "user_item";
        fixture.State.UserTranscriptItemId = "user_item";
        var handler = new ResponseLifecycleHandler();
        await handler.HandleAsync(
            new RealtimeResponseStartedEvent("event_8", "resp_1"),
            fixture.Context,
            CancellationToken.None);
        fixture.State.AssistantText.Append("hi there");

        var handled = await handler.HandleAsync(
            new RealtimeResponseFinishedEvent("event_9", "resp_1", "completed"),
            fixture.Context,
            CancellationToken.None);

        Assert.True(handled);
        Assert.True(fixture.Context.IsCompleted);
        Assert.Null(fixture.Context.CompletedResult.FailureReason);
        Assert.Equal("hi there", fixture.Context.CompletedResult.AssistantText);
    }

    [Fact]
    public async Task CompletedResponse_WaitsForAsynchronousInputTranscript()
    {
        var fixture = CreateFixture();
        var lifecycle = new ResponseLifecycleHandler();
        await lifecycle.HandleAsync(
            new RealtimeResponseStartedEvent("event_8b", "resp_1"),
            fixture.Context,
            CancellationToken.None);

        await lifecycle.HandleAsync(
            new RealtimeResponseFinishedEvent("event_8c", "resp_1", "completed"),
            fixture.Context,
            CancellationToken.None);

        Assert.False(fixture.Context.IsCompleted);
        Assert.True(fixture.State.ResponseFinishedPendingTranscript);

        await new TranscriptionHandler().HandleAsync(
            new RealtimeInputTranscriptionCompletedEvent("event_8d", "user_item", "hello"),
            fixture.Context,
            CancellationToken.None);

        Assert.True(fixture.Context.IsCompleted);
        Assert.Null(fixture.Context.CompletedResult.FailureReason);
        Assert.Equal("hello", fixture.Context.CompletedResult.UserTranscript);
    }

    [Theory]
    [InlineData("cancelled", "completed")]
    [InlineData("incomplete", "completed")]
    [InlineData("failed", "completed")]
    [InlineData("completed", "incomplete")]
    public async Task FunctionCall_DoesNotExecuteUnlessResponseAndItemCompleted(
        string responseStatus,
        string itemStatus)
    {
        var fixture = CreateFixture();
        var router = new FakeToolRouter();
        var handler = new FunctionCallHandler(router);

        await handler.HandleAsync(
            new RealtimeFunctionCallEvent(
                "event_fc_args",
                "resp_fc",
                "item_fc",
                null,
                "camera",
                "call_fc",
                "{}"),
            fixture.Context,
            CancellationToken.None);
        await handler.HandleAsync(
            new RealtimeFunctionCallEvent(
                "event_fc_item",
                "resp_fc",
                "item_fc",
                itemStatus,
                "camera",
                "call_fc",
                "{}"),
            fixture.Context,
            CancellationToken.None);
        await handler.HandleAsync(
            new RealtimeResponseFinishedEvent("event_fc_done", "resp_fc", responseStatus),
            fixture.Context,
            CancellationToken.None);

        Assert.Equal(0, router.ExecuteCount);
        Assert.Empty(fixture.Session.FunctionOutputs);
        Assert.Equal(0, fixture.Session.StartResponseCount);
    }

    [Fact]
    public async Task FunctionCall_DoesNotExecuteForIgnoredInterruptedResponse()
    {
        var fixture = CreateFixture();
        var router = new FakeToolRouter();
        var handler = new FunctionCallHandler(router);
        fixture.State.IgnoredResponseIds.Add("resp_interrupted");

        await handler.HandleAsync(
            new RealtimeFunctionCallEvent(
                "event_item_done",
                "resp_interrupted",
                "item_interrupted",
                "completed",
                "camera",
                "call_interrupted",
                "{}"),
            fixture.Context,
            CancellationToken.None);
        await handler.HandleAsync(
            new RealtimeResponseFinishedEvent(
                "event_response_done",
                "resp_interrupted",
                "completed"),
            fixture.Context,
            CancellationToken.None);

        Assert.Equal(0, router.ExecuteCount);
        Assert.Empty(fixture.Session.FunctionOutputs);
    }

    [Fact]
    public async Task BargeIn_DropsTrailingCancelledOutputAndSegmentsReplacementText()
    {
        var fixture = CreateFixture();
        var lifecycle = new ResponseLifecycleHandler();
        var streaming = new StreamingAudioHandler();
        var speech = new SpeechBoundaryHandler();

        await lifecycle.HandleAsync(
            new RealtimeResponseStartedEvent("event_old_start", "resp_old"),
            fixture.Context,
            CancellationToken.None);
        await streaming.HandleAsync(
            new RealtimeOutputTextDeltaEvent("event_old_text", "resp_old", "old answer"),
            fixture.Context,
            CancellationToken.None);
        await speech.HandleAsync(
            new RealtimeSpeechStartedEvent("event_barge", "user_item_2", TimeSpan.FromSeconds(1)),
            fixture.Context,
            CancellationToken.None);

        await streaming.HandleAsync(
            new RealtimeOutputTextDoneEvent("event_old_trailing", "resp_old", "old answer trailing"),
            fixture.Context,
            CancellationToken.None);
        await lifecycle.HandleAsync(
            new RealtimeResponseFinishedEvent("event_old_done", "resp_old", "cancelled"),
            fixture.Context,
            CancellationToken.None);
        await lifecycle.HandleAsync(
            new RealtimeResponseStartedEvent("event_new_start", "resp_new"),
            fixture.Context,
            CancellationToken.None);
        await streaming.HandleAsync(
            new RealtimeOutputTextDeltaEvent("event_new_text", "resp_new", "replacement"),
            fixture.Context,
            CancellationToken.None);

        Assert.Equal("replacement", fixture.State.AssistantText.ToString());
        Assert.DoesNotContain("resp_old", fixture.State.IgnoredResponseIds);
    }

    [Fact]
    public async Task InputTranscriptions_AreAppliedOnlyToTheActiveAudioItem()
    {
        var fixture = CreateFixture();
        var speech = new SpeechBoundaryHandler();
        var transcription = new TranscriptionHandler();

        await speech.HandleAsync(
            new RealtimeSpeechStartedEvent("event_input_1", "user_item_1", TimeSpan.Zero),
            fixture.Context,
            CancellationToken.None);
        await speech.HandleAsync(
            new RealtimeSpeechStartedEvent("event_input_2", "user_item_2", TimeSpan.FromSeconds(1)),
            fixture.Context,
            CancellationToken.None);

        await transcription.HandleAsync(
            new RealtimeInputTranscriptionFailedEvent(
                "event_late_failure_1",
                "user_item_1",
                "transcription_failed",
                "stale failure",
                null),
            fixture.Context,
            CancellationToken.None);
        Assert.Null(fixture.State.TranscriptionFailureReason);

        await transcription.HandleAsync(
            new RealtimeInputTranscriptionCompletedEvent("event_late_1", "user_item_1", "stale input"),
            fixture.Context,
            CancellationToken.None);
        Assert.Null(fixture.State.UserTranscript);

        await transcription.HandleAsync(
            new RealtimeInputTranscriptionCompletedEvent("event_current_2", "user_item_2", "current input"),
            fixture.Context,
            CancellationToken.None);

        Assert.Equal("current input", fixture.State.UserTranscript);
        Assert.Equal("user_item_2", fixture.State.UserTranscriptItemId);
    }

    [Fact]
    public void Adapter_MapsRecordedCorrelationAndTerminalStatusEvents()
    {
        var transcription = Assert.IsType<RealtimeInputTranscriptionCompletedEvent>(MapProtocolEvent("""
            {"type":"conversation.item.input_audio_transcription.completed","event_id":"evt_tx","item_id":"user_1","content_index":0,"transcript":"hello","usage":{"type":"tokens","total_tokens":1,"input_tokens":1,"output_tokens":0}}
            """));
        Assert.Equal("user_1", transcription.ItemId);

        var arguments = Assert.IsType<RealtimeFunctionCallEvent>(MapProtocolEvent("""
            {"type":"response.function_call_arguments.done","event_id":"evt_args","response_id":"resp_1","item_id":"item_1","output_index":0,"call_id":"call_1","name":"camera","arguments":"{}"}
            """));
        Assert.Equal("resp_1", arguments.ResponseId);
        Assert.Equal("item_1", arguments.ItemId);
        Assert.Null(arguments.ItemStatus);

        var itemDone = Assert.IsType<RealtimeFunctionCallEvent>(MapProtocolEvent("""
            {"type":"response.output_item.done","event_id":"evt_item","response_id":"resp_1","output_index":0,"item":{"id":"item_1","type":"function_call","status":"completed","name":"camera","call_id":"call_1","arguments":"{}"}}
            """));
        Assert.Equal("completed", itemDone.ItemStatus);

        var responseDone = Assert.IsType<RealtimeResponseFinishedEvent>(MapProtocolEvent("""
            {"type":"response.done","event_id":"evt_done","response":{"id":"resp_1","object":"realtime.response","status":"cancelled","status_details":{"type":"cancelled","reason":"client_cancelled"},"output":[],"conversation_id":"conv_1","output_modalities":["audio"]}}
            """));
        Assert.Equal("cancelled", responseDone.Status);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("cancelled")]
    [InlineData("failed")]
    [InlineData("incomplete")]
    public void Adapter_MapsEveryTerminalResponseStatus(string status)
    {
        var json = """
            {"type":"response.done","event_id":"evt_done","response":{"id":"resp_status","object":"realtime.response","status":"STATUS","output":[],"conversation_id":"conv_1","output_modalities":["audio"]}}
            """.Replace("STATUS", status, StringComparison.Ordinal);

        var responseDone = Assert.IsType<RealtimeResponseFinishedEvent>(MapProtocolEvent(json));

        Assert.Equal(status, responseDone.Status);
    }

    [Fact]
    public void Adapter_SerializesSessionImageCancellationAndTruncationShapes()
    {
        var configuration = CreateSessionConfiguration();
        using var sessionOptions = JsonDocument.Parse(Serialize(
            OpenAiRealtimeVoiceSession.BuildSessionOptions(configuration)));
        Assert.Equal("gpt-realtime", sessionOptions.RootElement.GetProperty("model").GetString());
        Assert.Equal(
            "gpt-4o-mini-transcribe",
            sessionOptions.RootElement
                .GetProperty("audio")
                .GetProperty("input")
                .GetProperty("transcription")
                .GetProperty("model")
                .GetString());
        Assert.True(sessionOptions.RootElement
            .GetProperty("audio")
            .GetProperty("input")
            .GetProperty("turn_detection")
            .GetProperty("interrupt_response")
            .GetBoolean());

        var largeImageDataUrl = "data:image/jpeg;base64," + new string('A', 800_000);
        using var imageCommand = JsonDocument.Parse(
            OpenAiRealtimeVoiceSession.BuildUserMessageCommand(
                new RealtimeInputMessage("inspect", largeImageDataUrl)));
        Assert.Equal("conversation.item.create", imageCommand.RootElement.GetProperty("type").GetString());
        var imageContent = imageCommand.RootElement.GetProperty("item").GetProperty("content");
        Assert.Equal("input_text", imageContent[0].GetProperty("type").GetString());
        Assert.Equal("inspect", imageContent[0].GetProperty("text").GetString());
        var imagePart = imageContent[1];
        Assert.Equal("input_image", imagePart.GetProperty("type").GetString());
        Assert.Equal(largeImageDataUrl, imagePart.GetProperty("image_url").GetString());

        using var cancellation = JsonDocument.Parse(Serialize(
            OpenAiRealtimeVoiceSession.BuildCancelCommand("resp_cancel")));
        Assert.Equal("response.cancel", cancellation.RootElement.GetProperty("type").GetString());
        Assert.Equal("resp_cancel", cancellation.RootElement.GetProperty("response_id").GetString());

        using var truncation = JsonDocument.Parse(Serialize(
            OpenAiRealtimeVoiceSession.BuildTruncationCommand("item_audio", 2, 1234)));
        Assert.Equal(
            "conversation.item.truncate",
            truncation.RootElement.GetProperty("type").GetString());
        Assert.Equal("item_audio", truncation.RootElement.GetProperty("item_id").GetString());
        Assert.Equal(2, truncation.RootElement.GetProperty("content_index").GetInt32());
        Assert.Equal(1234, truncation.RootElement.GetProperty("audio_end_ms").GetInt32());
    }

    [Fact]
    public async Task FatalStreamFailure_DisposesAndRecreatesRealtimeSession()
    {
        var first = new FakeRealtimeVoiceSession();
        var second = new FakeRealtimeVoiceSession();
        var factory = new FakeRealtimeVoiceSessionFactory(first, second);
        await using var manager = new RealtimeVoiceSessionManager(
            factory,
            NullLogger.Instance);

        await manager.EnsureConnectedAsync(CreateSessionConfiguration(), CancellationToken.None);
        var recovered = await manager.RecoverIfNeededAsync(
            "Realtime update stream failed: websocket closed",
            CreateSessionConfiguration(),
            CancellationToken.None);

        Assert.True(recovered);
        Assert.True(first.Disposed);
        Assert.Same(second, manager.Session);
        Assert.Equal(2, factory.ConnectCount);
        Assert.Equal(1, second.ConfigureCount);
    }

    [Fact]
    public async Task RealtimeError_ProducesFailedTurnResult()
    {
        var fixture = CreateFixture();

        var handled = await new ResponseLifecycleHandler().HandleAsync(
            new RealtimeErrorEvent("event_10", "invalid_value", "bad session", "session.audio"),
            fixture.Context,
            CancellationToken.None);

        Assert.True(handled);
        Assert.True(fixture.Context.IsCompleted);
        Assert.Contains("invalid_value", fixture.Context.CompletedResult.FailureReason);
        Assert.Contains("bad session", fixture.Context.CompletedResult.FailureReason);
    }

    [Fact]
    public async Task CameraTool_KeepsImageBytesOutOfFunctionOutput()
    {
        var capturedAt = DateTimeOffset.Parse("2026-07-18T23:42:15Z");
        var imageBytes = new byte[548_094];
        var camera = new CameraTool(
            new FakeCameraSnapshotProvider(
                new VisionCameraSnapshot(imageBytes, "image/jpeg", capturedAt)),
            new FakeMotionOrchestrator(),
            new RobotAppOptions(),
            NullLogger<CameraTool>.Instance);

        var result = await camera.ExecuteAsync(
            new ToolExecutionRequest(
                "call_camera",
                CameraTool.Name,
                "{\"question\":\"what is here?\"}",
                "session_1",
                "turn_1",
                ToolInvocationSource.Realtime),
            CancellationToken.None);

        Assert.True(result.OutputJson.Length < 1024);
        Assert.DoesNotContain("b64_im", result.OutputJson, StringComparison.Ordinal);
        Assert.DoesNotContain("image_data_url", result.OutputJson, StringComparison.Ordinal);
        Assert.Contains("\"ok\":true", result.OutputJson, StringComparison.Ordinal);
        Assert.True(Assert.Single(result.RealtimeInputs).ImageDataUrl!.Length > 700_000);
    }

    private static Fixture CreateFixture(
        int speechStopGraceMs = 300,
        Func<string, bool>? shutdownIntentDetector = null)
    {
        var state = new RealtimeTurnState
        {
            ActiveInputItemId = "user_item"
        };
        var session = new FakeRealtimeVoiceSession();
        var audio = new FakeRealtimeAudioOutput();
        var motion = new FakeMotionOrchestrator();
        var stateMachine = new FakeInteractionStateMachine();
        var context = new RealtimeTurnContext(
            state,
            session,
            audio,
            motion,
            stateMachine,
            NullLogger<RealtimeInteractionOrchestrator>.Instance,
            new AudioFormat(24000, 1, 16),
            24000,
            45000,
            speechStopGraceMs,
            false,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            shutdownIntentDetector ?? (_ => false));
        return new Fixture(context, state, session, audio, stateMachine);
    }

    private static RealtimeServerEvent MapProtocolEvent(string json)
    {
        var update = ModelReaderWriter.Read<RealtimeServerUpdate>(
            BinaryData.FromString(json),
            ModelReaderWriterOptions.Json);
        Assert.NotNull(update);
        return Assert.IsAssignableFrom<RealtimeServerEvent>(
            OpenAiRealtimeVoiceSession.MapServerUpdate(update));
    }

    private static string Serialize(object model)
        => ModelReaderWriter.Write(model, ModelReaderWriterOptions.Json).ToString();

    private static RealtimeSessionConfiguration CreateSessionConfiguration()
        => new(
            "gpt-realtime",
            "be helpful",
            "alloy",
            "gpt-4o-mini-transcribe",
            "en",
            24000,
            24000,
            []);

    private sealed record Fixture(
        RealtimeTurnContext Context,
        RealtimeTurnState State,
        FakeRealtimeVoiceSession Session,
        FakeRealtimeAudioOutput Audio,
        FakeInteractionStateMachine StateMachine);

    private sealed class FakeRealtimeVoiceSession : IRealtimeVoiceSession
    {
        public List<(string ItemId, int ContentIndex, int AudioEndMilliseconds)> Truncations { get; } = [];
        public List<string?> CancelledResponseIds { get; } = [];
        public List<(string CallId, string OutputJson)> FunctionOutputs { get; } = [];
        public List<RealtimeInputMessage> UserMessages { get; } = [];
        public int StartResponseCount { get; private set; }
        public int ConfigureCount { get; private set; }
        public bool Disposed { get; private set; }

        public Task ConfigureAsync(RealtimeSessionConfiguration configuration, CancellationToken cancellationToken)
        {
            ConfigureCount++;
            return Task.CompletedTask;
        }

        public Task SendInputAudioAsync(ReadOnlyMemory<byte> pcm16Audio, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task AddFunctionCallOutputAsync(string callId, string outputJson, CancellationToken cancellationToken)
        {
            FunctionOutputs.Add((callId, outputJson));
            return Task.CompletedTask;
        }

        public Task AddUserMessageAsync(RealtimeInputMessage message, CancellationToken cancellationToken)
        {
            UserMessages.Add(message);
            return Task.CompletedTask;
        }

        public Task StartResponseAsync(CancellationToken cancellationToken)
        {
            StartResponseCount++;
            return Task.CompletedTask;
        }

        public Task CancelResponseAsync(string? responseId, CancellationToken cancellationToken)
        {
            CancelledResponseIds.Add(responseId);
            return Task.CompletedTask;
        }

        public Task TruncateAudioAsync(
            string itemId,
            int contentIndex,
            int audioEndMilliseconds,
            CancellationToken cancellationToken)
        {
            Truncations.Add((itemId, contentIndex, audioEndMilliseconds));
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<RealtimeServerEvent> ReceiveEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeRealtimeVoiceSessionFactory(params FakeRealtimeVoiceSession[] sessions)
        : IRealtimeVoiceSessionFactory
    {
        private readonly Queue<FakeRealtimeVoiceSession> sessions = new(sessions);

        public int ConnectCount { get; private set; }

        public Task<IRealtimeVoiceSession> ConnectAsync(CancellationToken cancellationToken)
        {
            ConnectCount++;
            return Task.FromResult<IRealtimeVoiceSession>(sessions.Dequeue());
        }
    }

    private sealed class FakeRealtimeAudioOutput : IRealtimeAudioOutput
    {
        public bool Begun { get; private set; }
        public bool Cancelled { get; private set; }
        public List<byte[]> Writes { get; } = [];

        public void Begin(AudioFormat sourceFormat) => Begun = true;
        public void Write(byte[] pcmChunk, CancellationToken cancellationToken) => Writes.Add(pcmChunk);
        public void Complete() { }
        public void Cancel() => Cancelled = true;
    }

    private sealed class FakeMotionOrchestrator : IMotionOrchestrator
    {
        public void PushAssistantAudioPcm16(byte[] pcm16Bytes, int sampleRateHz, short channels = 1) { }
        public void ResetTalkingGesture() { }
        public void SetRobotMotionEnabled(bool enabled) { }
        public ValueTask<IAsyncDisposable> HoldCameraFocusAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IAsyncDisposable>(new NoOpLease());

        private sealed class NoOpLease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCameraSnapshotProvider(VisionCameraSnapshot snapshot)
        : ICameraSnapshotProvider
    {
        public Task<VisionCameraSnapshot?> CaptureSnapshotAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<VisionCameraSnapshot?>(snapshot);
    }

    private sealed class FakeInteractionStateMachine : IInteractionStateMachine
    {
        public InteractionState Current { get; private set; } = InteractionState.Idle;
        public void TransitionTo(InteractionState next, string reason) => Current = next;
    }

    private sealed class FakeToolRouter : IToolRouter
    {
        public int ExecuteCount { get; private set; }
        public bool HasAvailableTools => true;

        public IReadOnlyList<ToolDefinition> GetLegacyToolDefinitions() => [];
        public IReadOnlyList<RealtimeToolDefinition> GetRealtimeToolDefinitions() => [];
        public string BuildToolUsageGuidance() => string.Empty;

        public Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionRequest request,
            CancellationToken cancellationToken)
        {
            ExecuteCount++;
            return Task.FromResult(new ToolExecutionResult(
                "{\"ok\":true}",
                [],
                [new RealtimeInputMessage("inspect the image", "data:image/jpeg;base64,AQ==")],
                [],
                true));
        }
    }
}
