#pragma warning disable OPENAI002

using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI.Audio;
using OpenAI.RealtimeConversation;
using ReachTether.Audio;
using ReachTether.Audio.Alsa;
using ReachTether.WebRtc.Models;
using ReachyMini.Sdk;
using ReachyMini.Sdk.Exceptions;
using ReachyMini.Sdk.Models;

internal sealed record RealtimeTurnResult(
    string? UserTranscript,
    string AssistantText,
    bool StreamedAudioPlayback,
    string? FailureReason);

internal sealed class RealtimeInteractionOrchestrator(
    ReachyMiniClient reachyClient,
    LocalAudioSession audioSession,
    IAudioCapturePipeline audioCapture,
    IAudioPlaybackPipeline audioPlayback,
    IOpenAiTransport openAiTransport,
    RealtimeConversationClient realtimeClient,
    IInteractionStateMachine stateMachine,
    IHostApplicationLifetime appLifetime,
    RobotAppOptions options,
    ILogger<RealtimeInteractionOrchestrator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine("=== Chatty Reachy Mini (Realtime) ===");
        Console.WriteLine("Voice-enabled AI assistant for Reachy Mini using OpenAI realtime audio.\n");

        var defaultSystemPrompt = @"You are Reachy Mini, a friendly and helpful humanoid robot assistant.
You have expressive antennas that move to show emotions, and you can move your head and body.
Keep responses brief and conversational (1-2 sentences).
Be enthusiastic, curious, and engaging. Use simple language.";
        var boredTeenSystemPrompt = "Speak like a bored Gen Z teen. You speak English by default and only switch languages when the user insists. Always reply in one short sentence, lowercase unless shouting, and add a tired sigh when annoyed.";
        var systemPrompt = defaultSystemPrompt;

        var neutralPose = new GotoModelRequest
        {
            Antennas = [Deg(0), Deg(0)],
            Duration = 1.0,
            Interpolation = InterpolationMode.Minjerk
        };
        var continueConversation = true;
        var stopHostOnExit = false;
        var audioConnected = false;
        var stateChangedHandler = (Action<ReachySessionState>)(state => Console.WriteLine($"[LocalAudio] State changed -> {state}"));

        try
        {
            Console.WriteLine("Waking up Reachy Mini...");
            await reachyClient.Move.WakeUpAsync();
            await Task.Delay(2000, stoppingToken);

            Console.WriteLine("Connecting local ALSA audio session...");
            await audioSession.ConnectAsync(stoppingToken);
            audioConnected = true;
            audioSession.StateChanged += stateChangedHandler;

            var status = await reachyClient.Daemon.GetStatusAsync();
            Console.WriteLine($"Reachy Mini '{status.RobotName}' is ready!\n");

            await reachyClient.Move.GotoAsync(neutralPose);

            using var realtimeSession = await realtimeClient.StartConversationSessionAsync(stoppingToken);
            await realtimeSession.ConfigureSessionAsync(BuildSessionOptions(systemPrompt), stoppingToken);
            await using var updates = realtimeSession.ReceiveUpdatesAsync(stoppingToken).GetAsyncEnumerator(stoppingToken);

            Console.WriteLine($"Realtime model: {options.RealtimeModel}");
            Console.WriteLine("Conversation mode is active.");
            Console.WriteLine("Voice activity detection is enabled. Speak naturally to start recording.");
            Console.WriteLine("Say 'goodbye' or 'exit' to end the conversation.\n");
            Console.WriteLine("Say 'bored' to switch to bored-teen personality, or 'normal' to restore default personality.\n");
            Console.WriteLine(
                $"VAD settings: mode=server_vad, listenTimeout={options.Vad.ListenTimeoutMs}ms, responseTimeout={options.Realtime.ResponseTimeoutMs}ms");
            Console.WriteLine("Speech input path: ALSA capture worker -> realtime input_audio_buffer.append");
            Console.WriteLine("Speech output path: realtime websocket -> PCM stream -> ALSA sink\n");

            while (!stoppingToken.IsCancellationRequested && continueConversation)
            {
                audioPlayback.Flush();
                stateMachine.TransitionTo(InteractionState.Listening, "awaiting next user turn");

                Console.WriteLine("Listening... speak now.");

                var listeningPose = new GotoModelRequest
                {
                    Antennas = [Deg(10), Deg(10)],
                    Duration = 0.9,
                    Interpolation = InterpolationMode.Minjerk
                };
                await reachyClient.Move.GotoAsync(listeningPose);

                var turnResult = await RunRealtimeTurnAsync(
                    realtimeSession,
                    updates,
                    stoppingToken);

                if (!string.IsNullOrWhiteSpace(turnResult.FailureReason))
                {
                    Console.WriteLine($"Realtime turn failed: {turnResult.FailureReason}");
                    Console.WriteLine("Please try again.\n");

                    var confusedPose = new GotoModelRequest
                    {
                        Antennas = [Deg(-8), Deg(8)],
                        Duration = 0.8,
                        Interpolation = InterpolationMode.Minjerk
                    };
                    await reachyClient.Move.GotoAsync(confusedPose);
                    await Task.Delay(500, stoppingToken);
                    await reachyClient.Move.GotoAsync(neutralPose);
                    stateMachine.TransitionTo(InteractionState.Idle, "realtime turn failure");
                    continue;
                }

                var userInput = turnResult.UserTranscript?.Trim();
                if (!string.IsNullOrWhiteSpace(userInput))
                {
                    Console.WriteLine($"You: {userInput}");
                }
                else
                {
                    Console.WriteLine("You: [voice input]");
                }

                var loweredInput = userInput?.ToLowerInvariant();
                if (loweredInput == "bored")
                {
                    systemPrompt = boredTeenSystemPrompt;
                    await realtimeSession.ConfigureSessionAsync(BuildSessionOptions(systemPrompt), stoppingToken);
                    Console.WriteLine("Reachy: Switched personality to bored teen.");

                    stateMachine.TransitionTo(InteractionState.Speaking, "personality confirmation");
                    var wav = await openAiTransport.GenerateSpeechWaveAsync("switched to bored mode.", options.SpeechVoice, stoppingToken);
                    await audioPlayback.PlayAsync(wav, stoppingToken);

                    await reachyClient.Move.GotoAsync(neutralPose);
                    stateMachine.TransitionTo(InteractionState.Idle, "personality set");
                    Console.WriteLine();
                    continue;
                }

                if (loweredInput == "normal")
                {
                    systemPrompt = defaultSystemPrompt;
                    await realtimeSession.ConfigureSessionAsync(BuildSessionOptions(systemPrompt), stoppingToken);
                    Console.WriteLine("Reachy: Switched personality to normal.");

                    stateMachine.TransitionTo(InteractionState.Speaking, "personality confirmation");
                    var wav = await openAiTransport.GenerateSpeechWaveAsync("back to normal mode.", options.SpeechVoice, stoppingToken);
                    await audioPlayback.PlayAsync(wav, stoppingToken);

                    await reachyClient.Move.GotoAsync(neutralPose);
                    stateMachine.TransitionTo(InteractionState.Idle, "personality set");
                    Console.WriteLine();
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(userInput) && IsShutdownIntent(userInput))
                {
                    var farewellPose = new GotoModelRequest
                    {
                        Antennas = [Deg(-10), Deg(-10)],
                        Duration = 1.0,
                        Interpolation = InterpolationMode.Minjerk
                    };
                    await reachyClient.Move.GotoAsync(farewellPose);

                    var farewellText = "Goodbye! It was nice talking with you. I'm going to sleep now.";
                    Console.WriteLine($"Reachy: {farewellText}");

                    stateMachine.TransitionTo(InteractionState.Speaking, "farewell");
                    var wav = await openAiTransport.GenerateSpeechWaveAsync(farewellText, options.SpeechVoice, stoppingToken);
                    await audioPlayback.PlayAsync(wav, stoppingToken);

                    continueConversation = false;
                    stateMachine.TransitionTo(InteractionState.Idle, "conversation ended");
                    continue;
                }

                var responseText = string.IsNullOrWhiteSpace(turnResult.AssistantText)
                    ? (turnResult.StreamedAudioPlayback
                        ? "[audio response]"
                        : "I had trouble finding the right words. Could you ask again?")
                    : turnResult.AssistantText.Trim();
                Console.WriteLine($"Reachy: {responseText}");

                if (!turnResult.StreamedAudioPlayback)
                {
                    // Fallback to normal TTS if a text-only realtime response is returned.
                    var speakingPose = new GotoModelRequest
                    {
                        Antennas = [Deg(16), Deg(16)],
                        Duration = 1.1,
                        Interpolation = InterpolationMode.Minjerk
                    };
                    await reachyClient.Move.GotoAsync(speakingPose);

                    stateMachine.TransitionTo(InteractionState.Speaking, "fallback tts playback");
                    var fallbackWav = await openAiTransport.GenerateSpeechWaveAsync(responseText, options.SpeechVoice, stoppingToken);
                    await audioPlayback.PlayAsync(fallbackWav, stoppingToken);
                }

                await reachyClient.Move.GotoAsync(neutralPose);
                stateMachine.TransitionTo(InteractionState.Idle, "turn complete");
                Console.WriteLine();
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                stopHostOnExit = true;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during host shutdown (e.g., CTRL+C).
        }
        catch (Exception ex)
        {
            stateMachine.TransitionTo(InteractionState.Interrupted, "fatal exception");
            Console.WriteLine($"\nError ({ex.GetType().Name}): {ex.Message}");

            if (ex is ReachyMiniApiException apiEx)
            {
                Console.WriteLine($"Reachy API response: {apiEx.ResponseContent}");
            }

            if (!string.IsNullOrWhiteSpace(ex.StackTrace))
            {
                Console.WriteLine("Stack trace:");
                Console.WriteLine(ex.StackTrace);
            }

            stopHostOnExit = true;
        }
        finally
        {
            audioSession.StateChanged -= stateChangedHandler;
            await ShutdownAsync(audioConnected);

            if (stopHostOnExit)
            {
                appLifetime.StopApplication();
            }
        }
    }

    private ConversationSessionOptions BuildSessionOptions(string instructions)
    {
        return new ConversationSessionOptions
        {
            Instructions = instructions,
            ContentModalities = ConversationContentModalities.Audio,
            Voice = MapVoice(options.SpeechVoice),
            InputAudioFormat = ConversationAudioFormat.Pcm16,
            OutputAudioFormat = ConversationAudioFormat.Pcm16,
            InputTranscriptionOptions = new ConversationInputTranscriptionOptions
            {
                Model = (ConversationTranscriptionModel)options.TranscriptionModel
            },
            TurnDetectionOptions = ConversationTurnDetectionOptions.CreateServerVoiceActivityTurnDetectionOptions(
                null,
                null,
                null)
        };
    }

    private async Task<RealtimeTurnResult> RunRealtimeTurnAsync(
        RealtimeConversationSession session,
        IAsyncEnumerator<ConversationUpdate> updates,
        CancellationToken cancellationToken)
    {
        audioCapture.FlushBufferedFrames();

        var assistantText = new StringBuilder();
        string? userTranscript = null;
        string? activeResponseId = null;
        var outputFormat = new AudioFormat(options.Realtime.OutputSampleRateHz, 1, 16);
        var streamOpen = false;
        var streamFinalized = false;
        var streamedAudioPlayback = false;
        var speechStarted = false;
        var speechStopped = false;
        var responseStarted = false;
        var dropActiveResponseAudio = false;

        var listenDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(options.Vad.ListenTimeoutMs);
        var responseDeadline = DateTime.MaxValue;

        var sendAudioEnabled = 1;
        using var sendAudioCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sendAudioToken = sendAudioCts.Token;
        var sendAudioTask = Task.Run(async () =>
        {
            while (!sendAudioToken.IsCancellationRequested)
            {
                if (Volatile.Read(ref sendAudioEnabled) == 0)
                {
                    await Task.Delay(10, sendAudioToken);
                    continue;
                }

                var frame = await audioCapture.ReadFrameAsync(sendAudioToken);
                var monoChunk = frame.Format.Channels > 1
                    ? DownmixToMonoPcm16(frame.Pcm16Bytes, frame.Format.Channels)
                    : frame.Pcm16Bytes;
                if (monoChunk.Length == 0)
                {
                    continue;
                }

                await session.SendInputAudioAsync(new BinaryData(monoChunk), sendAudioToken);
            }
        }, CancellationToken.None);

        RealtimeTurnResult BuildFailure(string reason) =>
            new(userTranscript, assistantText.ToString(), streamedAudioPlayback, reason);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (sendAudioTask.IsFaulted)
                {
                    var sendError = sendAudioTask.Exception?.GetBaseException().Message ?? "unknown capture/send failure";
                    return BuildFailure($"Realtime input streaming failed: {sendError}");
                }

                var now = DateTime.UtcNow;
                if (!speechStarted && now >= listenDeadline)
                {
                    return BuildFailure("No speech detected before listen timeout.");
                }
                if (responseStarted && now >= responseDeadline)
                {
                    return BuildFailure($"Timed out waiting for realtime response after {options.Realtime.ResponseTimeoutMs}ms.");
                }

                ConversationUpdate update;
                try
                {
                    var timeout = TimeSpan.FromMilliseconds(250);
                    if (!speechStarted)
                    {
                        var untilListenTimeout = listenDeadline - now;
                        timeout = untilListenTimeout < timeout ? untilListenTimeout : timeout;
                    }
                    else if (responseStarted)
                    {
                        var untilResponseTimeout = responseDeadline - now;
                        timeout = untilResponseTimeout < timeout ? untilResponseTimeout : timeout;
                    }

                    if (timeout <= TimeSpan.Zero)
                    {
                        timeout = TimeSpan.FromMilliseconds(1);
                    }

                    if (!await MoveNextAsync(updates, timeout, cancellationToken))
                    {
                        return BuildFailure("Realtime update stream closed unexpectedly.");
                    }

                    update = updates.Current;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    continue;
                }

                switch (update)
                {
                    case ConversationInputSpeechStartedUpdate:
                        speechStarted = true;

                        if (responseStarted && streamOpen && !streamFinalized)
                        {
                            audioSession.CancelPlaybackStream();
                            streamOpen = false;
                            streamFinalized = true;
                            dropActiveResponseAudio = true;
                            stateMachine.TransitionTo(InteractionState.Listening, "barge-in");
                        }
                        break;

                    case ConversationInputSpeechFinishedUpdate:
                        if (!speechStopped)
                        {
                            speechStopped = true;
                            Interlocked.Exchange(ref sendAudioEnabled, 0);
                            Console.WriteLine("Reachy is thinking...");
                            stateMachine.TransitionTo(InteractionState.Thinking, "server vad speech stopped");
                        }
                        break;

                    case ConversationInputTranscriptionFinishedUpdate inputUpdate:
                        userTranscript = inputUpdate.Transcript?.Trim();
                        break;

                    case ConversationInputTranscriptionFailedUpdate inputFailure:
                        logger.LogWarning(
                            "Realtime input transcription failed: code={Code}, message={Message}, param={Param}",
                            inputFailure.ErrorCode,
                            inputFailure.ErrorMessage,
                            inputFailure.ErrorParameterName);
                        break;

                    case ConversationResponseStartedUpdate started:
                        activeResponseId = started.ResponseId;
                        responseStarted = true;
                        dropActiveResponseAudio = false;
                        responseDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(options.Realtime.ResponseTimeoutMs);
                        break;

                    case ConversationItemStreamingPartDeltaUpdate delta:
                        if (!string.IsNullOrWhiteSpace(activeResponseId)
                            && !string.Equals(delta.ResponseId, activeResponseId, StringComparison.Ordinal))
                        {
                            break;
                        }

                        var audioChunk = delta.AudioBytes.ToArray();
                        if (audioChunk.Length > 0 && !dropActiveResponseAudio)
                        {
                            if (!streamOpen)
                            {
                                audioSession.BeginPlaybackStream(outputFormat);
                                streamOpen = true;
                                streamedAudioPlayback = true;
                                stateMachine.TransitionTo(InteractionState.Speaking, "realtime streaming audio");
                            }

                            audioSession.WritePlaybackPcm16Chunk(audioChunk, cancellationToken);
                        }

                        if (!string.IsNullOrWhiteSpace(delta.Text))
                        {
                            assistantText.Append(delta.Text);
                        }
                        else if (!string.IsNullOrWhiteSpace(delta.AudioTranscript))
                        {
                            assistantText.Append(delta.AudioTranscript);
                        }
                        break;

                    case ConversationItemStreamingPartFinishedUpdate finishedPart:
                        if (!string.IsNullOrWhiteSpace(finishedPart.Text) && assistantText.Length == 0)
                        {
                            assistantText.Append(finishedPart.Text);
                        }
                        else if (!string.IsNullOrWhiteSpace(finishedPart.AudioTranscript) && assistantText.Length == 0)
                        {
                            assistantText.Append(finishedPart.AudioTranscript);
                        }
                        break;

                    case ConversationErrorUpdate errorUpdate:
                        return BuildFailure($"Realtime API error: {errorUpdate.ErrorCode}: {errorUpdate.Message}");

                    case ConversationResponseFinishedUpdate finished:
                        if (string.IsNullOrWhiteSpace(activeResponseId)
                            || string.Equals(finished.ResponseId, activeResponseId, StringComparison.Ordinal))
                        {
                            if (streamOpen)
                            {
                                audioSession.CompletePlaybackStream();
                                streamFinalized = true;
                            }

                            var normalizedAssistantText = assistantText.ToString().Trim();
                            return new RealtimeTurnResult(
                                userTranscript,
                                normalizedAssistantText,
                                streamedAudioPlayback,
                                null);
                        }
                        break;
                }
            }
        }
        finally
        {
            sendAudioCts.Cancel();
            try
            {
                await sendAudioTask;
            }
            catch (OperationCanceledException) when (sendAudioToken.IsCancellationRequested)
            {
                // Expected during turn teardown.
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Realtime capture/send loop stopped with an error.");
            }

            if (streamOpen && !streamFinalized)
            {
                try
                {
                    audioSession.CancelPlaybackStream();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to cancel realtime playback stream cleanly.");
                }
            }
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private static async Task<bool> MoveNextAsync(
        IAsyncEnumerator<ConversationUpdate> updates,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        return await updates.MoveNextAsync().AsTask().WaitAsync(timeoutCts.Token);
    }

    private static byte[] DownmixToMonoPcm16(byte[] pcmBytes, short channels)
    {
        if (channels <= 1 || pcmBytes.Length < 2)
        {
            return pcmBytes;
        }

        var bytesPerSample = 2;
        var frameSize = channels * bytesPerSample;
        var frameCount = pcmBytes.Length / frameSize;
        if (frameCount <= 0)
        {
            return [];
        }

        var mono = new byte[frameCount * bytesPerSample];
        var source = pcmBytes.AsSpan();
        var destination = mono.AsSpan();

        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            var frameOffset = frameIndex * frameSize;
            var mixed = 0;

            for (var channel = 0; channel < channels; channel++)
            {
                var sampleOffset = frameOffset + (channel * bytesPerSample);
                mixed += BinaryPrimitives.ReadInt16LittleEndian(source.Slice(sampleOffset, bytesPerSample));
            }

            var monoSample = (short)(mixed / channels);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(frameIndex * bytesPerSample, bytesPerSample), monoSample);
        }

        return mono;
    }

    private static ConversationVoice MapVoice(GeneratedSpeechVoice voice)
    {
        var name = voice.ToString();
        return name switch
        {
            "Echo" => ConversationVoice.Echo,
            "Shimmer" => ConversationVoice.Shimmer,
            _ => ConversationVoice.Alloy
        };
    }

    private async Task ShutdownAsync(bool audioConnected)
    {
        audioPlayback.Flush();
        using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var shutdownToken = shutdownCts.Token;

        Console.WriteLine("\nPutting Reachy Mini to sleep...");
        try
        {
            await reachyClient.Move.GotoSleepAsync(shutdownToken);
            await Task.Delay(2000, shutdownToken);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Sleep command timed out during shutdown.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Sleep command failed during shutdown: {ex.Message}");
        }

        if (audioConnected)
        {
            try
            {
                await audioSession.DisconnectAsync(shutdownToken);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Audio disconnect timed out during shutdown.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Audio disconnect failed during shutdown: {ex.Message}");
            }
        }

        Console.WriteLine("Reachy Mini is now sleeping. Goodbye!");
    }

    private static bool IsShutdownIntent(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        return ShutdownKeywordPattern.IsMatch(input)
            || EndConversationPattern.IsMatch(input)
            || DonePattern.IsMatch(input);
    }

    private static readonly Regex ShutdownKeywordPattern = new(
        @"^\s*(?:please\s+)?(?:reachy[\s,]+)?(?:goodbye|bye|exit|quit|shutdown|shut\s*down|go\s+to\s+sleep|sleep)\s*(?:now)?[.!?]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex EndConversationPattern = new(
        @"^\s*(?:please\s+)?(?:end|stop|close)\s+(?:the\s+)?(?:conversation|chat|session)\s*(?:now)?[.!?]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DonePattern = new(
        @"^\s*(?:we(?:'re| are)\s+done|that(?:'s| is)\s+all)\s*[.!?]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static double Deg(double degrees) => degrees * Math.PI / 180.0;
}
