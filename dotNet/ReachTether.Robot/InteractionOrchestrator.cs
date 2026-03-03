using Microsoft.Extensions.Hosting;
using OpenAI.Audio;
using OpenAI.Chat;
using ReachTether.Audio.Alsa;
using ReachTether.WebRtc.Models;
using ReachyMini.Sdk;
using ReachyMini.Sdk.Exceptions;
using ReachyMini.Sdk.Models;
using System.Text.RegularExpressions;

internal sealed class InteractionOrchestrator(
    ReachyMiniClient reachyClient,
    LocalAudioSession audioSession,
    IAudioCapturePipeline audioCapture,
    IAudioPlaybackPipeline audioPlayback,
    IOpenAiTransport openAiTransport,
    IPersonalityCatalog personalities,
    IInteractionStateMachine stateMachine,
    IMotionOrchestrator motionOrchestrator,
    IHostApplicationLifetime appLifetime,
    RobotAppOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine("=== Chatty Reachy Mini ===");
        Console.WriteLine("Voice-enabled AI assistant for Reachy Mini Robot using openai-dotnet.\n");

        var activePersonality = personalities.DefaultPersonality;
        var systemPrompt = activePersonality.Instructions;
        motionOrchestrator.SetRobotMotionEnabled(false);

        var conversationHistory = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt)
        };

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

            await reachyClient.Move.GotoAsync(neutralPose);
            motionOrchestrator.SetRobotMotionEnabled(true);

            Console.WriteLine("Conversation mode is active.");
            Console.WriteLine("Voice activity detection is enabled. Speak naturally to start recording.");
            Console.WriteLine("Say 'goodbye' or 'exit' to end the conversation.\n");
            Console.WriteLine(
                $"Active personality: {activePersonality.DisplayName} ({activePersonality.Id}).");
            Console.WriteLine(
                "Switch personality by saying a configured shortcut (for example, 'bored' or 'normal') or 'personality <name>'.");
            Console.WriteLine($"Available personalities: {string.Join(", ", personalities.All.Select(p => p.Id))}\n");
            Console.WriteLine(
                $"VAD settings: preRoll={options.Vad.PreRollMs}ms, startFrames={options.Vad.StartTriggerFrames}, endSilence={options.Vad.EndSilenceMs}ms, maxUtterance={options.Vad.MaxUtteranceMs}ms, timeout={options.Vad.ListenTimeoutMs}ms");
            Console.WriteLine("Speech input path: ALSA capture worker -> bounded channel -> transcribe transport");
            Console.WriteLine("Speech output path: TTS transport -> playback worker channel -> ALSA playback\n");

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

                var captureResult = await audioCapture.CaptureUtteranceAsync(stoppingToken);
                if (!captureResult.SpeechDetected)
                {
                    Console.WriteLine($"No speech detected: {captureResult.FailureReason}");
                    Console.WriteLine("Please try again.\n");

                    var confusedPose = new GotoModelRequest
                    {
                        Antennas = [Deg(-8), Deg(8)],
                        Duration = 0.8,
                        Interpolation = InterpolationMode.Minjerk
                    };
                    await reachyClient.Move.GotoAsync(confusedPose);
                    await Task.Delay(300, stoppingToken);
                    await reachyClient.Move.GotoAsync(neutralPose);
                    stateMachine.TransitionTo(InteractionState.Idle, "vad timeout");
                    continue;
                }

                var transcriptionResult = await openAiTransport.TranscribeAsync(captureResult.Frames, options.TranscriptionLanguage, stoppingToken);
                var userInput = transcriptionResult.Text;

                if (string.IsNullOrWhiteSpace(userInput))
                {
                    Console.WriteLine(
                        $"Speech not recognized: {transcriptionResult.FailureReason} (stage={transcriptionResult.Stage}, frames={transcriptionResult.FrameCount}, pcmBytes={transcriptionResult.PcmBytes}, captureMs={captureResult.DurationMs}).");
                }

                if (string.IsNullOrWhiteSpace(userInput))
                {
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
                    stateMachine.TransitionTo(InteractionState.Idle, "empty input");
                    continue;
                }

                Console.WriteLine($"You: {userInput}");
                if (personalities.TryResolveSwitchCommand(userInput, out var selectedPersonality))
                {
                    activePersonality = selectedPersonality;
                    systemPrompt = activePersonality.Instructions;
                    conversationHistory[0] = new SystemChatMessage(systemPrompt);
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

                if (IsShutdownIntent(userInput))
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

                conversationHistory.Add(new UserChatMessage(userInput));

                Console.WriteLine("Reachy is thinking...");
                stateMachine.TransitionTo(InteractionState.Thinking, "chat completion");

                var thinkingPose = new GotoModelRequest
                {
                    Antennas = [Deg(12), Deg(-12)],
                    Duration = 1.0,
                    Interpolation = InterpolationMode.Minjerk
                };
                await reachyClient.Move.GotoAsync(thinkingPose);

                var response = await openAiTransport.CompleteChatAsync(conversationHistory, stoppingToken);
                conversationHistory.Add(new AssistantChatMessage(response));

                if (conversationHistory.Count > 15)
                {
                    conversationHistory =
                    [
                        conversationHistory[0],
                        .. conversationHistory.Skip(conversationHistory.Count - 12)
                    ];
                }

                Console.WriteLine($"Reachy: {response}");

                var speakingPose = new GotoModelRequest
                {
                    Antennas = [Deg(16), Deg(16)],
                    Duration = 1.1,
                    Interpolation = InterpolationMode.Minjerk
                };
                await reachyClient.Move.GotoAsync(speakingPose);

                stateMachine.TransitionTo(InteractionState.Speaking, "tts playback");
                var responseWav = await openAiTransport.GenerateSpeechWaveAsync(response, options.SpeechVoice, stoppingToken);
                await audioPlayback.PlayAsync(responseWav, stoppingToken);

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
            audioSession.StateChanged -= stateChangedHandler;
            await ShutdownAsync(audioConnected);

            if (stopHostOnExit)
            {
                appLifetime.StopApplication();
            }
        }
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
