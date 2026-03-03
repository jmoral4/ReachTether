using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Audio;
using OpenAI.Chat;
using ReachTether.Audio;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

internal sealed record TranscriptionCaptureResult(
    string? Text,
    string Stage,
    string? FailureReason,
    int FrameCount,
    long PcmBytes);

internal sealed record AudioClients(
    AudioClient Transcription,
    AudioClient Speech);

internal interface IOpenAiTransport
{
    Task<TranscriptionCaptureResult> TranscribeAsync(
        AudioFrame[] frames,
        string language,
        CancellationToken cancellationToken = default);

    Task<string> CompleteChatAsync(
        IReadOnlyList<ChatMessage> conversation,
        CancellationToken cancellationToken = default);

    Task<byte[]> GenerateSpeechWaveAsync(
        string text,
        GeneratedSpeechVoice voice,
        CancellationToken cancellationToken = default);
}

internal sealed class OpenAiTransport(
    OpenAIClient openAIClient,
    RobotAppOptions appOptions,
    AudioClients audioClients,
    OpenAiResponsesClient responsesClient,
    ILogger<OpenAiTransport> logger) : IOpenAiTransport
{
    public async Task<TranscriptionCaptureResult> TranscribeAsync(
        AudioFrame[] frames,
        string language,
        CancellationToken cancellationToken = default)
    {
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
            await File.WriteAllBytesAsync(tempFilePath, wavBytes, cancellationToken);

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

            var primaryText = await TryTranscribeTextAsync(audioClients.Transcription, tempFilePath, options, cancellationToken);
            if (!string.IsNullOrWhiteSpace(primaryText))
            {
                return new TranscriptionCaptureResult(primaryText, "transcribe", null, frames.Length, pcmBuffer.Length);
            }

            if (!string.Equals(appOptions.TranscriptionModel, "whisper-1", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    logger.LogWarning(
                        "Primary transcription model '{Model}' returned empty text; retrying with whisper-1.",
                        appOptions.TranscriptionModel);
                    var fallbackClient = openAIClient.GetAudioClient("whisper-1");
                    var fallbackText = await TryTranscribeTextAsync(fallbackClient, tempFilePath, options, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(fallbackText))
                    {
                        return new TranscriptionCaptureResult(fallbackText, "transcribe-fallback", null, frames.Length, pcmBuffer.Length);
                    }
                }
                catch (Exception fallbackEx)
                {
                    return new TranscriptionCaptureResult(
                        null,
                        "transcribe-fallback",
                        $"Fallback transcription with whisper-1 failed: {fallbackEx.GetType().Name}: {fallbackEx.Message}",
                        frames.Length,
                        pcmBuffer.Length);
                }
            }

            return new TranscriptionCaptureResult(
                null,
                "transcribe",
                "Transcription returned empty text.",
                frames.Length,
                pcmBuffer.Length);
        }
        catch (Exception ex)
        {
            if (ShouldRetryWithWhisper(ex) &&
                !string.Equals(appOptions.TranscriptionModel, "whisper-1", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    logger.LogWarning(
                        ex,
                        "Transcription failed for model '{Model}'. Retrying with whisper-1.",
                        appOptions.TranscriptionModel);
                    var options = new AudioTranscriptionOptions
                    {
                        Language = language,
                        ResponseFormat = AudioTranscriptionFormat.Simple
                    };
                    var fallbackClient = openAIClient.GetAudioClient("whisper-1");
                    var fallbackText = await TryTranscribeTextAsync(fallbackClient, tempFilePath, options, cancellationToken);

                    if (!string.IsNullOrWhiteSpace(fallbackText))
                    {
                        return new TranscriptionCaptureResult(fallbackText, "transcribe-fallback", null, frames.Length, pcmBuffer.Length);
                    }

                    return new TranscriptionCaptureResult(
                        null,
                        "transcribe-fallback",
                        "Fallback transcription with whisper-1 returned empty text.",
                        frames.Length,
                        pcmBuffer.Length);
                }
                catch (Exception fallbackEx)
                {
                    return new TranscriptionCaptureResult(
                        null,
                        "transcribe-fallback",
                        $"Transcription API failed and fallback failed: {ex.GetType().Name}: {ex.Message} | fallback={fallbackEx.GetType().Name}: {fallbackEx.Message}",
                        frames.Length,
                        pcmBuffer.Length);
                }
            }

            return new TranscriptionCaptureResult(
                null,
                "transcribe",
                BuildTranscriptionErrorMessage(ex),
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
                // Best-effort cleanup.
            }
        }
    }

    public async Task<string> CompleteChatAsync(
        IReadOnlyList<ChatMessage> conversation,
        CancellationToken cancellationToken = default)
    {
        var input = BuildResponsesInput(conversation);
        var instructions = ExtractSystemInstructions(conversation);

        try
        {
            return await CompleteWithResponsesApiAsync(
                appOptions.ChatModel,
                input,
                instructions,
                cancellationToken);
        }
        catch (Exception primaryEx)
        {
            if (!string.IsNullOrWhiteSpace(appOptions.ChatFallbackModel) &&
                !string.Equals(appOptions.ChatModel, appOptions.ChatFallbackModel, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    logger.LogWarning(
                        primaryEx,
                        "Responses API failed for primary model '{PrimaryModel}'. Retrying with fallback model '{FallbackModel}'.",
                        appOptions.ChatModel,
                        appOptions.ChatFallbackModel);

                    return await CompleteWithResponsesApiAsync(
                        appOptions.ChatFallbackModel,
                        input,
                        instructions,
                        cancellationToken);
                }
                catch (Exception fallbackEx)
                {
                    logger.LogError(
                        fallbackEx,
                        "Responses API failed for fallback model '{FallbackModel}' after primary model '{PrimaryModel}' failed.",
                        appOptions.ChatFallbackModel,
                        appOptions.ChatModel);
                }
            }
            else
            {
                logger.LogError(
                    primaryEx,
                    "Responses API failed for model '{Model}'.",
                    appOptions.ChatModel);
            }
        }

        return "I ran into a model error while thinking. Please try again.";
    }

    public async Task<byte[]> GenerateSpeechWaveAsync(
        string text,
        GeneratedSpeechVoice voice,
        CancellationToken cancellationToken = default)
    {
        var speechOptions = new SpeechGenerationOptions
        {
            ResponseFormat = GeneratedSpeechFormat.Wav
        };

        var speechResult = await audioClients.Speech.GenerateSpeechAsync(text, voice, speechOptions, cancellationToken);
        return speechResult.Value.ToArray();
    }

    private static async Task<string?> TryTranscribeTextAsync(
        AudioClient client,
        string filePath,
        AudioTranscriptionOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transcription = await client.TranscribeAudioAsync(filePath, options);
        return transcription.Value.Text?.Trim();
    }

    private static bool ShouldRetryWithWhisper(Exception ex)
    {
        var message = ex.Message;
        return message.Contains("Invalid URL (POST /v1/audio/transcriptions)", StringComparison.OrdinalIgnoreCase)
            || message.Contains("404", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildTranscriptionErrorMessage(Exception ex)
    {
        var baseMessage = $"Transcription API failed: {ex.GetType().Name}: {ex.Message}";
        if (ex.Message.Contains("Invalid URL (POST /v1/audio/transcriptions)", StringComparison.OrdinalIgnoreCase))
        {
            return $"{baseMessage}. This endpoint mismatch is often resolved by using model 'whisper-1' for transcription.";
        }

        return baseMessage;
    }

    private async Task<string> CompleteWithResponsesApiAsync(
        string model,
        string input,
        string? instructions,
        CancellationToken cancellationToken)
    {
        var payload = new ResponsesRequest(
            model,
            input,
            instructions);

        using var response = await responsesClient.HttpClient.PostAsJsonAsync("responses", payload, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Responses API failed for model '{model}' (status={(int)response.StatusCode}): {ExtractResponsesErrorMessage(responseBody)}");
        }

        var output = TryExtractResponseOutputText(responseBody);
        if (!string.IsNullOrWhiteSpace(output))
        {
            return output;
        }

        throw new InvalidOperationException($"Responses API returned empty output_text for model '{model}'.");
    }

    private static string BuildResponsesInput(IReadOnlyList<ChatMessage> conversation)
    {
        var builder = new StringBuilder();

        foreach (var message in conversation)
        {
            if (message is SystemChatMessage)
            {
                continue;
            }

            var text = ExtractMessageText(message);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            builder.Append(RoleLabel(message));
            builder.Append(": ");
            builder.AppendLine(text);
        }

        var input = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(input) ? "Say hello." : input;
    }

    private static string? ExtractSystemInstructions(IReadOnlyList<ChatMessage> conversation)
    {
        foreach (var message in conversation)
        {
            if (message is not SystemChatMessage)
            {
                continue;
            }

            var text = ExtractMessageText(message);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static string ExtractMessageText(ChatMessage message)
    {
        var textParts = message.Content
            .Where(part => part.Kind == ChatMessageContentPartKind.Text && !string.IsNullOrWhiteSpace(part.Text))
            .Select(part => part.Text!.Trim());

        return string.Join("\n", textParts).Trim();
    }

    private static string RoleLabel(ChatMessage message)
    {
        return message switch
        {
            SystemChatMessage => "system",
            UserChatMessage => "user",
            AssistantChatMessage => "assistant",
            _ => "user"
        };
    }

    private static string TryExtractResponseOutputText(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;

        if (root.TryGetProperty("output_text", out var outputTextProperty))
        {
            var direct = outputTextProperty.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct;
            }
        }

        if (!root.TryGetProperty("output", out var outputArray) || outputArray.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var fragments = new List<string>();
        foreach (var outputItem in outputArray.EnumerateArray())
        {
            if (!outputItem.TryGetProperty("type", out var itemType) ||
                !string.Equals(itemType.GetString(), "message", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!outputItem.TryGetProperty("content", out var contentArray) || contentArray.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var contentItem in contentArray.EnumerateArray())
            {
                if (!contentItem.TryGetProperty("type", out var contentType) ||
                    !string.Equals(contentType.GetString(), "output_text", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (contentItem.TryGetProperty("text", out var textProperty))
                {
                    var text = textProperty.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        fragments.Add(text);
                    }
                }
            }
        }

        return string.Join("\n", fragments).Trim();
    }

    private static string ExtractResponsesErrorMessage(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var errorProperty) &&
                errorProperty.TryGetProperty("message", out var messageProperty))
            {
                var message = messageProperty.GetString();
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }
            }
        }
        catch
        {
            // Fall through to raw body return.
        }

        return string.IsNullOrWhiteSpace(responseBody)
            ? "No error body returned."
            : responseBody.Trim();
    }

    private sealed record ResponsesRequest(
        string Model,
        string Input,
        string? Instructions);
}
