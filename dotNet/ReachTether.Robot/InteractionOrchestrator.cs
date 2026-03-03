using Microsoft.Extensions.Hosting;
using OpenAI.Audio;
using OpenAI.Chat;
using ReachTether.Audio.Alsa;
using ReachyMini.Sdk;
using ReachyMini.Sdk.Exceptions;
using ReachyMini.Sdk.Models;

internal sealed class InteractionOrchestrator(
    ReachyMiniClient reachyClient,
    LocalAudioSession audioSession,
    IAudioCapturePipeline audioCapture,
    IAudioPlaybackPipeline audioPlayback,
    IOpenAiTransport openAiTransport,
    IInteractionStateMachine stateMachine,
    RobotAppOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine("=== Chatty Reachy Mini ===");
        Console.WriteLine("Voice-enabled AI assistant for Reachy Mini Robot using openai-dotnet.\n");

        var defaultSystemPrompt = @"You are Reachy Mini, a friendly and helpful humanoid robot assistant.
You have expressive antennas that move to show emotions, and you can move your head and body.
Keep responses brief and conversational (1-2 sentences).
Be enthusiastic, curious, and engaging. Use simple language.";
        var boredTeenSystemPrompt = "Speak like a bored Gen Z teen. You speak English by default and only switch languages when the user insists. Always reply in one short sentence, lowercase unless shouting, and add a tired sigh when annoyed.";
        var systemPrompt = defaultSystemPrompt;

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

        try
        {
            Console.WriteLine("Waking up Reachy Mini...");
            await reachyClient.Move.WakeUpAsync();
            await Task.Delay(2000, stoppingToken);

            Console.WriteLine("Connecting local ALSA audio session...");
            await audioSession.ConnectAsync(stoppingToken);
            audioSession.StateChanged += state => Console.WriteLine($"[LocalAudio] State changed -> {state}");

            var status = await reachyClient.Daemon.GetStatusAsync();
            Console.WriteLine($"Reachy Mini '{status.RobotName}' is ready!\n");

            await reachyClient.Move.GotoAsync(neutralPose);

            Console.WriteLine("Conversation mode is active.");
            Console.WriteLine($"Press ENTER to record a {options.RecordingSeconds}-second audio clip.");
            Console.WriteLine("Say 'goodbye' or 'exit' to end the conversation.\n");
            Console.WriteLine("Type 'bored' to switch to bored-teen personality, or 'normal' to restore default personality.\n");
            Console.WriteLine("Speech input path: ALSA capture worker -> bounded channel -> transcribe transport");
            Console.WriteLine("Speech output path: TTS transport -> playback worker channel -> ALSA playback\n");

            var continueConversation = true;

            while (!stoppingToken.IsCancellationRequested && continueConversation)
            {
                audioPlayback.Flush();
                stateMachine.TransitionTo(InteractionState.Listening, "awaiting next user turn");

                Console.WriteLine($"Listening... Press ENTER to begin {options.RecordingSeconds}-second recording.");
                Console.WriteLine("Or type a message directly (type 'goodbye' or 'exit' to quit).");

                var listeningPose = new GotoModelRequest
                {
                    Antennas = [Deg(10), Deg(10)],
                    Duration = 0.9,
                    Interpolation = InterpolationMode.Minjerk
                };
                await reachyClient.Move.GotoAsync(listeningPose);

                var typedInput = Console.ReadLine();
                string? userInput;
                if (!string.IsNullOrWhiteSpace(typedInput))
                {
                    userInput = typedInput.Trim();
                }
                else
                {
                    var captureFrames = await audioCapture.CaptureWindowAsync(TimeSpan.FromSeconds(options.RecordingSeconds), stoppingToken);
                    var transcriptionResult = await openAiTransport.TranscribeAsync(captureFrames, options.TranscriptionLanguage, stoppingToken);
                    userInput = transcriptionResult.Text;

                    if (string.IsNullOrWhiteSpace(userInput))
                    {
                        Console.WriteLine(
                            $"Speech not recognized: {transcriptionResult.FailureReason} (stage={transcriptionResult.Stage}, frames={transcriptionResult.FrameCount}, pcmBytes={transcriptionResult.PcmBytes}).");
                    }
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

                var loweredInput = userInput.ToLowerInvariant();
                if (loweredInput == "bored")
                {
                    systemPrompt = boredTeenSystemPrompt;
                    conversationHistory[0] = new SystemChatMessage(systemPrompt);
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
                    conversationHistory[0] = new SystemChatMessage(systemPrompt);
                    Console.WriteLine("Reachy: Switched personality to normal.");

                    stateMachine.TransitionTo(InteractionState.Speaking, "personality confirmation");
                    var wav = await openAiTransport.GenerateSpeechWaveAsync("back to normal mode.", options.SpeechVoice, stoppingToken);
                    await audioPlayback.PlayAsync(wav, stoppingToken);

                    await reachyClient.Move.GotoAsync(neutralPose);
                    stateMachine.TransitionTo(InteractionState.Idle, "personality set");
                    Console.WriteLine();
                    continue;
                }

                if (loweredInput.Contains("goodbye") || loweredInput.Contains("exit") || loweredInput.Contains("bye"))
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

            Console.WriteLine("\nPutting Reachy Mini to sleep...");
            await reachyClient.Move.GotoSleepAsync();
            await Task.Delay(2000, stoppingToken);
            await audioSession.DisconnectAsync(stoppingToken);
            Console.WriteLine("Reachy Mini is now sleeping. Goodbye!");
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

            try
            {
                audioPlayback.Flush();
                await audioSession.DisconnectAsync(stoppingToken);
                await reachyClient.Move.GotoSleepAsync();
            }
            catch
            {
                // Ignore cleanup errors.
            }
        }
    }

    private static double Deg(double degrees) => degrees * Math.PI / 180.0;
}
