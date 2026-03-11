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
    IPersonalityCatalog personalities,
    IInteractionStateMachine stateMachine,
    IMotionOrchestrator motionOrchestrator,
    VisionStartupProbe visionStartupProbe,
    IHostApplicationLifetime appLifetime,
    RobotAppOptions options,
    ILogger<RealtimeInteractionOrchestrator> logger) : BackgroundService
{
    private static readonly IRealtimeEventHandler[] RealtimeEventHandlers = CreateRealtimeEventHandlers();

    private static IRealtimeEventHandler[] CreateRealtimeEventHandlers()
    {
        IRealtimeEventHandler[] handlers =
        [
            new SpeechBoundaryHandler(),
            new TranscriptionHandler(),
            new StreamingAudioHandler(),
            new ResponseLifecycleHandler()
        ];

        Array.Sort(handlers, static (left, right) => left.Order.CompareTo(right.Order));
        return handlers;
    }

    private enum InputChannelSelection
    {
        Average,
        Channel0,
        Channel1
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine("=== Chatty Reachy Mini (Realtime) ===");
        Console.WriteLine("Voice-enabled AI assistant for Reachy Mini using OpenAI realtime audio.\n");

        var activePersonality = personalities.DefaultPersonality;
        var systemPrompt = activePersonality.Instructions;
        motionOrchestrator.SetRobotMotionEnabled(false);

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
        RealtimeConversationSession? realtimeSession = null;
        IAsyncEnumerator<ConversationUpdate>? updates = null;

        async Task EnsureRealtimeSessionAsync()
        {
            if (realtimeSession is not null && updates is not null)
            {
                return;
            }

            realtimeSession = await realtimeClient.StartConversationSessionAsync(stoppingToken);
            await realtimeSession.ConfigureSessionAsync(BuildSessionOptions(systemPrompt), stoppingToken);
            updates = realtimeSession.ReceiveUpdatesAsync(stoppingToken).GetAsyncEnumerator(stoppingToken);
        }

        async Task ResetRealtimeSessionAsync(string reason, bool logWarning = true)
        {
            if (logWarning)
            {
                logger.LogWarning("Resetting realtime session: {Reason}", reason);
            }

            if (updates is not null)
            {
                try
                {
                    await updates.DisposeAsync();
                }
                catch (NotSupportedException)
                {
                    // Some SDK async enumerators don't support DisposeAsync().
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to dispose realtime update stream cleanly.");
                }
                finally
                {
                    updates = null;
                }
            }

            if (realtimeSession is not null)
            {
                try
                {
                    realtimeSession.Dispose();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to dispose realtime session cleanly.");
                }
                finally
                {
                    realtimeSession = null;
                }
            }
        }

        try
        {
            Console.WriteLine("Waking up Reachy Mini...");
            using (var wakeUpCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                wakeUpCts.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    await reachyClient.Move.WakeUpAsync(wakeUpCts.Token);
                }
                catch (OperationCanceledException) when (wakeUpCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                {
                    throw new TimeoutException("Timed out waiting for Reachy wake-up response after 20s.");
                }
            }
            await Task.Delay(2000, stoppingToken);

            Console.WriteLine("Connecting local ALSA audio session...");
            await audioSession.ConnectAsync(stoppingToken);
            audioConnected = true;
            audioSession.StateChanged += stateChangedHandler;

            var status = await reachyClient.Daemon.GetStatusAsync();
            Console.WriteLine($"Reachy Mini '{status.RobotName}' is ready!\n");

            await visionStartupProbe.RunAfterRobotReadyAsync(stoppingToken);
            if (options.Vision.ProbeOnly)
            {
                return;
            }

            await reachyClient.Move.GotoAsync(neutralPose);
            motionOrchestrator.SetRobotMotionEnabled(true);
            await EnsureRealtimeSessionAsync();

            Console.WriteLine($"Realtime model: {options.RealtimeModel}");
            Console.WriteLine("Conversation mode is active.");
            Console.WriteLine("Voice activity detection is enabled. Speak naturally to start recording.");
            Console.WriteLine("Say 'goodbye' or 'exit' to end the conversation.\n");
            Console.WriteLine(
                $"Active personality: {activePersonality.DisplayName} ({activePersonality.Id}).");
            Console.WriteLine(
                "Switch personality by saying a configured shortcut (for example, 'bored' or 'normal') or 'personality <name>'.");
            Console.WriteLine($"Available personalities: {string.Join(", ", personalities.All.Select(p => p.Id))}\n");
            var inputChannelSelection = ParseInputChannelSelection(options.Realtime.InputChannelStrategy);
            Console.WriteLine(
                $"VAD settings: mode=server_vad (OpenAI defaults), listenTimeout={options.Vad.ListenTimeoutMs}ms, responseTimeout={options.Realtime.ResponseTimeoutMs}ms");
            Console.WriteLine(
                $"Realtime input settings: captureRate={options.AudioSampleRateHz}Hz, outboundRate={options.Realtime.InputSampleRateHz}Hz, channelStrategy={inputChannelSelection}, speechStopGrace={options.Realtime.SpeechStopMicDisableGraceMs}ms, transcriptGate={options.Realtime.RequireTranscriptBeforeAssistantAudio}");
            Console.WriteLine("Speech input path: ALSA capture worker -> mono/ch-select -> optional resample -> realtime input_audio_buffer.append");
            Console.WriteLine("Speech output path: realtime websocket -> PCM stream -> ALSA sink\n");

            while (!stoppingToken.IsCancellationRequested && continueConversation)
            {
                await EnsureRealtimeSessionAsync();

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
                    realtimeSession!,
                    updates!,
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

                    if (ShouldResetSessionOnFailure(turnResult.FailureReason))
                    {
                        await ResetRealtimeSessionAsync($"turn failure: {turnResult.FailureReason}");
                    }

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

                if (!string.IsNullOrWhiteSpace(userInput)
                    && personalities.TryResolveSwitchCommand(userInput, out var selectedPersonality))
                {
                    activePersonality = selectedPersonality;
                    systemPrompt = activePersonality.Instructions;
                    await realtimeSession!.ConfigureSessionAsync(BuildSessionOptions(systemPrompt), stoppingToken);
                    Console.WriteLine($"Reachy: Switched personality to {activePersonality.DisplayName}.");

                    stateMachine.TransitionTo(InteractionState.Speaking, "personality confirmation");
                    var wav = await openAiTransport.GenerateSpeechWaveAsync(
                        $"switched personality to {activePersonality.DisplayName}.",
                        options.SpeechVoice,
                        stoppingToken);
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
                    try
                    {
                        await audioPlayback.PlayAsync(wav, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Farewell playback failed; continuing shutdown.");
                    }

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
            motionOrchestrator.SetRobotMotionEnabled(false);
            await ResetRealtimeSessionAsync("application shutdown", logWarning: false);
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
            ContentModalities = ConversationContentModalities.Audio | ConversationContentModalities.Text,
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
        motionOrchestrator.ResetTalkingGesture();

        var inputChannelSelection = ParseInputChannelSelection(options.Realtime.InputChannelStrategy);
        var realtimeInputRateHz = options.Realtime.InputSampleRateHz;
        var logAverageDownmixLevels =
            options.Realtime.LogAverageDownmixInputLevels && inputChannelSelection == InputChannelSelection.Average;
        var benignRealtimeErrorCodes = new HashSet<string>(
            options.Realtime.BenignErrorCodes,
            StringComparer.OrdinalIgnoreCase);
        var outputFormat = new AudioFormat(options.Realtime.OutputSampleRateHz, 1, 16);
        var turnState = new RealtimeTurnState();
        var turnContext = new RealtimeTurnContext(
            turnState,
            audioSession,
            motionOrchestrator,
            stateMachine,
            logger,
            outputFormat,
            options.Realtime.OutputSampleRateHz,
            options.Realtime.ResponseTimeoutMs,
            options.Realtime.SpeechStopMicDisableGraceMs,
            options.Realtime.RequireTranscriptBeforeAssistantAudio,
            benignRealtimeErrorCodes,
            IsShutdownIntent);
        Task<bool>? pendingUpdateTask = null;

        var listenDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(options.Vad.ListenTimeoutMs);

        using var sendAudioCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sendAudioToken = sendAudioCts.Token;
        var sendAudioTask = Task.Run(async () =>
        {
            double levelSumSquares = 0;
            long levelSampleCount = 0;
            int levelPeak = 0;
            var nextInputLevelLogUtc = DateTime.UtcNow + TimeSpan.FromSeconds(2);

            while (!sendAudioToken.IsCancellationRequested)
            {
                if (Volatile.Read(ref turnState.SendAudioEnabled) == 0)
                {
                    await Task.Delay(10, sendAudioToken);
                    continue;
                }

                var frame = await audioCapture.ReadFrameAsync(sendAudioToken);
                var monoChunk = BuildMonoRealtimeInputPcm16(
                    frame.Pcm16Bytes,
                    frame.Format.Channels,
                    inputChannelSelection);
                if (monoChunk.Length == 0)
                {
                    continue;
                }

                if (logAverageDownmixLevels)
                {
                    AccumulatePcm16Levels(monoChunk, ref levelSumSquares, ref levelSampleCount, ref levelPeak);
                    var now = DateTime.UtcNow;
                    if (now >= nextInputLevelLogUtc && levelSampleCount > 0)
                    {
                        var rms = Math.Sqrt(levelSumSquares / levelSampleCount) / short.MaxValue;
                        var peak = Math.Min(1.0, levelPeak / (double)short.MaxValue);
                        logger.LogDebug(
                            "Realtime input downmix levels: rms={Rms:F4}, peak={Peak:F4}",
                            rms,
                            peak);
                        levelSumSquares = 0;
                        levelSampleCount = 0;
                        levelPeak = 0;
                        nextInputLevelLogUtc = now + TimeSpan.FromSeconds(2);
                    }
                }

                var outboundChunk = ResampleMonoPcm16(
                    monoChunk,
                    frame.Format.SampleRateHz,
                    realtimeInputRateHz);
                if (outboundChunk.Length == 0)
                {
                    continue;
                }

                await session.SendInputAudioAsync(new BinaryData(outboundChunk), sendAudioToken);
            }
        }, CancellationToken.None);

        RealtimeTurnResult BuildFailure(string reason)
        {
            logger.LogWarning(
                "Realtime turn failed: {Reason} (speechStarted={SpeechStarted}, speechStopped={SpeechStopped}, responseStarted={ResponseStarted})",
                reason,
                turnState.SpeechStarted,
                turnState.SpeechStopped,
                turnState.ResponseStarted);
            turnContext.CompleteFailure(reason);
            return turnContext.CompletedResult;
        }

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
                if (turnState.PendingMicDisableDeadlineUtc is DateTime micDisableDeadlineUtc
                    && now >= micDisableDeadlineUtc)
                {
                    turnContext.DisableMicSendAndTransitionToThinking("server vad speech stopped (grace elapsed)");
                }

                if (!turnState.SpeechStarted && now >= listenDeadline)
                {
                    return BuildFailure("No speech detected before listen timeout.");
                }
                if (turnState.ResponseStarted && now >= turnState.ResponseDeadlineUtc)
                {
                    return BuildFailure($"Timed out waiting for realtime response after {options.Realtime.ResponseTimeoutMs}ms.");
                }

                ConversationUpdate update;
                var timeout = TimeSpan.FromMilliseconds(250);
                if (!turnState.SpeechStarted)
                {
                    var untilListenTimeout = listenDeadline - now;
                    timeout = untilListenTimeout < timeout ? untilListenTimeout : timeout;
                }
                else if (turnState.ResponseStarted)
                {
                    var untilResponseTimeout = turnState.ResponseDeadlineUtc - now;
                    timeout = untilResponseTimeout < timeout ? untilResponseTimeout : timeout;
                }

                if (timeout <= TimeSpan.Zero)
                {
                    timeout = TimeSpan.FromMilliseconds(1);
                }

                pendingUpdateTask ??= updates.MoveNextAsync().AsTask();
                var completedTask = await Task.WhenAny(pendingUpdateTask, Task.Delay(timeout, cancellationToken));
                if (!ReferenceEquals(completedTask, pendingUpdateTask))
                {
                    continue;
                }

                bool hasUpdate;
                try
                {
                    hasUpdate = await pendingUpdateTask;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return BuildFailure($"Realtime update stream failed: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    pendingUpdateTask = null;
                }

                if (!hasUpdate)
                {
                    return BuildFailure("Realtime update stream closed unexpectedly.");
                }

                update = updates.Current;

                foreach (var handler in RealtimeEventHandlers)
                {
                    await handler.HandleAsync(update, turnContext, cancellationToken);
                    if (turnContext.IsCompleted)
                    {
                        return turnContext.CompletedResult;
                    }
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

            if (turnState.StreamOpen && !turnState.StreamFinalized)
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

    private static bool ShouldResetSessionOnFailure(string failureReason)
    {
        if (string.IsNullOrWhiteSpace(failureReason))
        {
            return false;
        }

        return failureReason.StartsWith("Realtime API error:", StringComparison.OrdinalIgnoreCase)
            || failureReason.StartsWith("Realtime update stream failed:", StringComparison.OrdinalIgnoreCase)
            || failureReason.StartsWith("Realtime input streaming failed:", StringComparison.OrdinalIgnoreCase)
            || failureReason.Contains("stream closed unexpectedly", StringComparison.OrdinalIgnoreCase);
    }

    private static InputChannelSelection ParseInputChannelSelection(string configuredSelection)
    {
        var normalized = configuredSelection.Trim().ToLowerInvariant();
        return normalized switch
        {
            "average" or "avg" or "mix" or "downmix" => InputChannelSelection.Average,
            "channel1" or "ch1" or "right" => InputChannelSelection.Channel1,
            _ => InputChannelSelection.Channel0
        };
    }

    private static byte[] BuildMonoRealtimeInputPcm16(
        byte[] pcmBytes,
        short channels,
        InputChannelSelection selection)
    {
        if (channels <= 1 || pcmBytes.Length < 2)
        {
            return pcmBytes;
        }

        return selection switch
        {
            InputChannelSelection.Average => DownmixAverageToMonoPcm16(pcmBytes, channels),
            InputChannelSelection.Channel1 when channels > 1 => SelectChannelToMonoPcm16(pcmBytes, channels, 1),
            _ => SelectChannelToMonoPcm16(pcmBytes, channels, 0)
        };
    }

    private static byte[] SelectChannelToMonoPcm16(byte[] pcmBytes, short channels, int selectedChannel)
    {
        var bytesPerSample = 2;
        var frameSize = channels * bytesPerSample;
        var frameCount = pcmBytes.Length / frameSize;
        if (frameCount <= 0)
        {
            return [];
        }

        var channelIndex = Math.Clamp(selectedChannel, 0, channels - 1);
        var mono = new byte[frameCount * bytesPerSample];
        var source = pcmBytes.AsSpan();
        var destination = mono.AsSpan();

        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            var frameOffset = frameIndex * frameSize;
            var sampleOffset = frameOffset + (channelIndex * bytesPerSample);
            var sample = BinaryPrimitives.ReadInt16LittleEndian(source.Slice(sampleOffset, bytesPerSample));
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(frameIndex * bytesPerSample, bytesPerSample), sample);
        }

        return mono;
    }

    private static byte[] DownmixAverageToMonoPcm16(byte[] pcmBytes, short channels)
    {
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

    private static byte[] ResampleMonoPcm16(byte[] pcmBytes, int sourceRateHz, int targetRateHz)
    {
        if (sourceRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceRateHz), "Source sample rate must be greater than zero.");
        }
        if (targetRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetRateHz), "Target sample rate must be greater than zero.");
        }
        if (sourceRateHz == targetRateHz || pcmBytes.Length < 4)
        {
            return pcmBytes;
        }

        const int bytesPerSample = 2;
        var sourceSamples = pcmBytes.Length / bytesPerSample;
        if (sourceSamples <= 1)
        {
            return pcmBytes;
        }

        var targetSamples = Math.Max(1, (int)Math.Round(sourceSamples * (double)targetRateHz / sourceRateHz));
        var output = new byte[targetSamples * bytesPerSample];
        var source = pcmBytes.AsSpan();
        var destination = output.AsSpan();

        for (var targetIndex = 0; targetIndex < targetSamples; targetIndex++)
        {
            var sourcePosition = targetIndex * (double)sourceRateHz / targetRateHz;
            var sourceIndex = (int)Math.Floor(sourcePosition);
            var nextIndex = Math.Min(sourceIndex + 1, sourceSamples - 1);
            var fraction = sourcePosition - sourceIndex;

            var a = BinaryPrimitives.ReadInt16LittleEndian(source.Slice(sourceIndex * bytesPerSample, bytesPerSample));
            var b = BinaryPrimitives.ReadInt16LittleEndian(source.Slice(nextIndex * bytesPerSample, bytesPerSample));
            var interpolated = a + ((b - a) * fraction);
            var sample = (short)Math.Clamp((int)Math.Round(interpolated), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(targetIndex * bytesPerSample, bytesPerSample), sample);
        }

        return output;
    }

    private static void AccumulatePcm16Levels(
        ReadOnlySpan<byte> pcmBytes,
        ref double sumSquares,
        ref long sampleCount,
        ref int peak)
    {
        if (pcmBytes.Length < 2)
        {
            return;
        }

        const int bytesPerSample = 2;

        for (var offset = 0; offset + bytesPerSample <= pcmBytes.Length; offset += bytesPerSample)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcmBytes.Slice(offset, bytesPerSample));
            var magnitude = Math.Abs((int)sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }

            sumSquares += (double)sample * sample;
            sampleCount++;
        }
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
