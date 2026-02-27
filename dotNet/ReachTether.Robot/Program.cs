using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Audio;
using OpenAI.Chat;
using ReachTether.Audio;
using ReachTether.Audio.Alsa;
using ReachyMini.Sdk;
using ReachyMini.Sdk.Configuration;
using ReachyMini.Sdk.Exceptions;
using ReachyMini.Sdk.Models;
using ReachTether.WebRtc.Abstractions;

LoadDotEnvIfPresent();

var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
    .Build();

var openAIApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    ?? throw new Exception("OPENAI_API_KEY not found in .env file or environment variables.");

var chatModel = configuration["OpenAI:ChatModel"] ?? "gpt-4o-mini";
var transcriptionModel = configuration["OpenAI:TranscriptionModel"] ?? "gpt-4o-transcribe";
var speechModel = configuration["OpenAI:SpeechModel"] ?? "gpt-4o-mini-tts";
var speechVoiceName = configuration["OpenAI:SpeechVoice"] ?? "alloy";
var transcriptionLanguage = configuration["OpenAI:TranscriptionLanguage"] ?? "en";
var recordingSeconds = configuration.GetValue("OpenAI:RecordingSeconds", 5);
var reachyBaseUrl = configuration["ReachyMini:BaseUrl"] ?? "http://localhost:8080";
var captureDevice = configuration["Audio:CaptureDevice"] ?? "reachymini_audio_src";
var playbackDevice = configuration["Audio:PlaybackDevice"] ?? "reachymini_audio_sink";

Console.WriteLine("=== Chatty Reachy Mini ===");
Console.WriteLine("Voice-enabled AI assistant for Reachy Mini Robot using openai-dotnet.\n");

var reachyOptions = Options.Create(new ReachyMiniOptions
{
    BaseUrl = reachyBaseUrl,
    Timeout = TimeSpan.FromSeconds(30)
});

using var httpClient = new HttpClient();
var reachyClient = new ReachyMiniClient(httpClient, reachyOptions);

var openAIClient = new OpenAIClient(openAIApiKey);
var chatClient = openAIClient.GetChatClient(chatModel);
var transcriptionClient = openAIClient.GetAudioClient(transcriptionModel);
var speechClient = openAIClient.GetAudioClient(speechModel);
var speechVoice = ParseSpeechVoice(speechVoiceName);
await using var audioSession = new LocalAudioSession(new LocalAudioOptions
{
    CaptureDevice = captureDevice,
    PlaybackDevice = playbackDevice,
    SampleRate = 16000,
    Channels = 2
});
audioSession.StateChanged += state =>
{
    Console.WriteLine($"[LocalAudio] State changed -> {state}");
};

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

try
{
    Console.WriteLine("Waking up Reachy Mini...");
    await reachyClient.Move.WakeUpAsync();
    await Task.Delay(2000);
    Console.WriteLine("Connecting local ALSA audio session...");
    await audioSession.ConnectAsync();

    var status = await reachyClient.Daemon.GetStatusAsync();
    Console.WriteLine($"Reachy Mini '{status.RobotName}' is ready!\n");

    var neutralPose = new GotoModelRequest
    {
        Antennas = [Deg(0), Deg(0)],
        Duration = 1.0,
        Interpolation = InterpolationMode.Minjerk
    };

    await reachyClient.Move.GotoAsync(neutralPose);

    Console.WriteLine("Conversation mode is active.");
    Console.WriteLine($"Press ENTER to record a {recordingSeconds}-second audio clip.");
    Console.WriteLine("Say 'goodbye' or 'exit' to end the conversation.\n");
    Console.WriteLine("Type 'bored' to switch to bored-teen personality, or 'normal' to restore default personality.\n");
    Console.WriteLine("Speech input path: ALSA capture -> CaptureFramesAsync -> WavePcm16.Encode -> OpenAI Transcribe");
    Console.WriteLine("Speech output path: OpenAI TTS -> GenerateSpeechAsync (wav) -> PlayWaveAsync -> ALSA playback\n");

    var continueConversation = true;

    while (continueConversation)
    {
        Console.WriteLine($"Listening... Press ENTER to begin {recordingSeconds}-second recording.");
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
            var transcriptionResult = await CaptureAndTranscribeAsync(
                audioSession,
                transcriptionClient,
                transcriptionLanguage,
                recordingSeconds);
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
            await Task.Delay(500);
            await reachyClient.Move.GotoAsync(neutralPose);
            continue;
        }

        Console.WriteLine($"You: {userInput}");

        var loweredInput = userInput.ToLowerInvariant();
        if (loweredInput == "bored")
        {
            systemPrompt = boredTeenSystemPrompt;
            conversationHistory[0] = new SystemChatMessage(systemPrompt);
            Console.WriteLine("Reachy: Switched personality to bored teen.");
            await GenerateAndPlaySpeechAsync(audioSession, speechClient, "switched to bored mode.", speechVoice, "personality");
            await reachyClient.Move.GotoAsync(neutralPose);
            Console.WriteLine();
            continue;
        }

        if (loweredInput == "normal")
        {
            systemPrompt = defaultSystemPrompt;
            conversationHistory[0] = new SystemChatMessage(systemPrompt);
            Console.WriteLine("Reachy: Switched personality to normal.");
            await GenerateAndPlaySpeechAsync(audioSession, speechClient, "back to normal mode.", speechVoice, "personality");
            await reachyClient.Move.GotoAsync(neutralPose);
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
            await GenerateAndPlaySpeechAsync(audioSession, speechClient, farewellText, speechVoice, "farewell");
            continueConversation = false;
            continue;
        }

        conversationHistory.Add(new UserChatMessage(userInput));

        Console.WriteLine("Reachy is thinking...");

        var thinkingPose = new GotoModelRequest
        {
            Antennas = [Deg(12), Deg(-12)],
            Duration = 1.0,
            Interpolation = InterpolationMode.Minjerk
        };
        await reachyClient.Move.GotoAsync(thinkingPose);

        var completion = await chatClient.CompleteChatAsync(conversationHistory);
        var response = completion.Value.Content.FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(response))
        {
            response = "I had trouble finding the right words. Could you ask again?";
        }

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

        await GenerateAndPlaySpeechAsync(audioSession, speechClient, response, speechVoice, "response");
        await reachyClient.Move.GotoAsync(neutralPose);
        Console.WriteLine();
    }

    Console.WriteLine("\nPutting Reachy Mini to sleep...");
    await reachyClient.Move.GotoSleepAsync();
    await Task.Delay(2000);

    Console.WriteLine("Reachy Mini is now sleeping. Goodbye!");
}
catch (Exception ex)
{
    Console.WriteLine($"\nError ({ex.GetType().Name}): {ex.Message}");

    if (ex is ReachyMiniApiException apiEx)
    {
        Console.WriteLine($"Reachy API response: {apiEx.ResponseContent}");
    }

    if (ex.InnerException != null)
    {
        Console.WriteLine($"Details: {ex.InnerException.Message}");
    }

    if (!string.IsNullOrWhiteSpace(ex.StackTrace))
    {
        Console.WriteLine("Stack trace:");
        Console.WriteLine(ex.StackTrace);
    }

    try
    {
        await reachyClient.Move.GotoSleepAsync();
    }
    catch
    {
        // Ignore cleanup errors
    }
}

static GeneratedSpeechVoice ParseSpeechVoice(string value)
{
    return value.Trim().ToLowerInvariant() switch
    {
        "alloy" => GeneratedSpeechVoice.Alloy,
        "echo" => GeneratedSpeechVoice.Echo,
        "fable" => GeneratedSpeechVoice.Fable,
        "onyx" => GeneratedSpeechVoice.Onyx,
        "nova" => GeneratedSpeechVoice.Nova,
        "shimmer" => GeneratedSpeechVoice.Shimmer,
        _ => GeneratedSpeechVoice.Alloy
    };
}

static double Deg(double degrees) => degrees * Math.PI / 180.0;

static async Task<TranscriptionCaptureResult> CaptureAndTranscribeAsync(
    IReachySession session,
    AudioClient transcriptionClient,
    string language,
    int recordingSeconds)
{
    if (recordingSeconds < 1)
    {
        recordingSeconds = 1;
    }

    Console.WriteLine($"Capturing {recordingSeconds} seconds from Reachy local audio stream...");
    var frames = await session.CaptureFramesAsync(TimeSpan.FromSeconds(recordingSeconds));
    if (frames.Length == 0)
    {
        return new TranscriptionCaptureResult(
            null,
            "capture",
            "No inbound audio frames were received from the local ALSA capture device.",
            0,
            0);
    }

    var firstFormat = frames[0].Format;
    using var pcmBuffer = new MemoryStream();
    var formatMismatchCount = 0;
    foreach (var frame in frames)
    {
        if (frame.Format != firstFormat)
        {
            formatMismatchCount++;
            continue;
        }

        pcmBuffer.Write(frame.Pcm16Bytes, 0, frame.Pcm16Bytes.Length);
    }

    if (pcmBuffer.Length < 1024)
    {
        return new TranscriptionCaptureResult(
            null,
            "capture",
            $"Captured audio too short for transcription (frames={frames.Length}, mismatchedFormatFrames={formatMismatchCount}, pcmBytes={pcmBuffer.Length}).",
            frames.Length,
            pcmBuffer.Length);
    }

    byte[] wavBytes;
    try
    {
        wavBytes = WavePcm16.Encode(pcmBuffer.ToArray(), firstFormat);
    }
    catch (Exception ex)
    {
        return new TranscriptionCaptureResult(
            null,
            "wav-encode",
            $"Failed to WAV-encode captured PCM: {ex.GetType().Name}: {ex.Message}",
            frames.Length,
            pcmBuffer.Length);
    }

    var tempFilePath = Path.Combine(Path.GetTempPath(), $"reachy-recording-{Guid.NewGuid():N}.wav");

    try
    {
        await File.WriteAllBytesAsync(tempFilePath, wavBytes);
    }
    catch (Exception ex)
    {
        return new TranscriptionCaptureResult(
            null,
            "file-write",
            $"Failed to write temporary WAV for transcription: {ex.GetType().Name}: {ex.Message}",
            frames.Length,
            pcmBuffer.Length);
    }

    try
    {
        var fileInfo = new FileInfo(tempFilePath);
        if (!fileInfo.Exists || fileInfo.Length < 128)
        {
            return new TranscriptionCaptureResult(
                null,
                "file-verify",
                $"Temporary WAV file is missing or too small (exists={fileInfo.Exists}, bytes={fileInfo.Length}).",
                frames.Length,
                pcmBuffer.Length);
        }

        var options = new AudioTranscriptionOptions
        {
            Language = language,
            ResponseFormat = AudioTranscriptionFormat.Simple
        };

        var transcription = await transcriptionClient.TranscribeAudioAsync(tempFilePath, options);
        var text = transcription.Value.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TranscriptionCaptureResult(
                null,
                "transcribe",
                "Transcription returned empty text.",
                frames.Length,
                pcmBuffer.Length);
        }

        return new TranscriptionCaptureResult(
            text,
            "transcribe",
            null,
            frames.Length,
            pcmBuffer.Length);
    }
    catch (Exception ex)
    {
        return new TranscriptionCaptureResult(
            null,
            "transcribe",
            $"Transcription API failed: {ex.GetType().Name}: {ex.Message}",
            frames.Length,
            pcmBuffer.Length);
    }
    finally
    {
        try
        {
            File.Delete(tempFilePath);
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}

static async Task GenerateAndPlaySpeechAsync(
    IReachySession session,
    AudioClient speechClient,
    string text,
    GeneratedSpeechVoice voice,
    string context)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return;
    }

    var speechOptions = new SpeechGenerationOptions
    {
        ResponseFormat = GeneratedSpeechFormat.Wav
    };

    var speechResult = await speechClient.GenerateSpeechAsync(text, voice, speechOptions);
    try
    {
        await session.PlayWaveAsync(speechResult.Value.ToArray());
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException(
            $"Failed during speech playback ({context}). Call path: GenerateSpeechAsync -> PlayWaveAsync -> ALSA playback device. {ex}",
            ex);
    }
}

static void LoadDotEnvIfPresent()
{
    var candidates = new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), ".env"),
        Path.Combine(AppContext.BaseDirectory, ".env")
    };

    foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        if (!File.Exists(path))
        {
            continue;
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            var value = line[(separatorIndex + 1)..].Trim();
            value = value.Trim('"').Trim('\'');

            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        break;
    }
}

internal sealed record TranscriptionCaptureResult(
    string? Text,
    string Stage,
    string? FailureReason,
    int FrameCount,
    long PcmBytes);
