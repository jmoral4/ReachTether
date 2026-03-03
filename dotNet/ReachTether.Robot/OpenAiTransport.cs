using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Audio;
using OpenAI.Chat;
using ReachTether.Audio;
using System.Net.Http.Json;
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

internal abstract record ChatCompletionResult;

internal sealed record TextResult(string Text) : ChatCompletionResult;

internal sealed record ToolCall(
    string Id,
    string Name,
    string ArgumentsJson);

internal sealed record ToolCallResult(
    IReadOnlyList<ToolCall> ToolCalls) : ChatCompletionResult;

internal sealed record ToolDefinition(
    string Name,
    string? Description,
    object Parameters,
    bool? Strict = null);

internal interface IOpenAiTransport
{
    Task<TranscriptionCaptureResult> TranscribeAsync(
        AudioFrame[] frames,
        string language,
        CancellationToken cancellationToken = default);

    Task<ChatCompletionResult> CompleteChatAsync(
        IReadOnlyList<ChatMessage> conversation,
        IReadOnlyList<ToolDefinition>? tools = null,
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

        if (wavBytes.Length < 128)
        {
            return new TranscriptionCaptureResult(
                null,
                "wav-verify",
                $"WAV payload is too small (bytes={wavBytes.Length}).",
                frames.Length,
                pcmBuffer.Length);
        }

        try
        {
            var options = new AudioTranscriptionOptions
            {
                Language = language,
                ResponseFormat = AudioTranscriptionFormat.Simple
            };

            var primaryText = await TryTranscribeTextAsync(audioClients.Transcription, wavBytes, options, cancellationToken);
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
                    var fallbackText = await TryTranscribeTextAsync(fallbackClient, wavBytes, options, cancellationToken);
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
                    var fallbackText = await TryTranscribeTextAsync(fallbackClient, wavBytes, options, cancellationToken);

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
    }

    public async Task<ChatCompletionResult> CompleteChatAsync(
        IReadOnlyList<ChatMessage> conversation,
        IReadOnlyList<ToolDefinition>? tools = null,
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
                tools,
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
                        tools,
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

        return new TextResult("I ran into a model error while thinking. Please try again.");
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
        byte[] wavBytes,
        AudioTranscriptionOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var wavStream = new MemoryStream(wavBytes, writable: false);
        var transcription = await client.TranscribeAudioAsync(wavStream, "audio.wav", options, cancellationToken);
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

    private async Task<ChatCompletionResult> CompleteWithResponsesApiAsync(
        string model,
        IReadOnlyList<ResponsesInputItem> input,
        string? instructions,
        IReadOnlyList<ToolDefinition>? tools,
        CancellationToken cancellationToken)
    {
        var payload = new ResponsesRequest(
            model,
            input,
            instructions,
            BuildResponsesTools(tools));

        using var response = await responsesClient.HttpClient.PostAsJsonAsync("responses", payload, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Responses API failed for model '{model}' (status={(int)response.StatusCode}): {ExtractResponsesErrorMessage(responseBody)}");
        }

        var result = TryExtractResponseResult(responseBody);
        if (result is not null)
        {
            return result;
        }

        throw new InvalidOperationException($"Responses API returned no text or tool calls for model '{model}'.");
    }

    private static IReadOnlyList<ResponsesInputItem> BuildResponsesInput(IReadOnlyList<ChatMessage> conversation)
    {
        var input = new List<ResponsesInputItem>();

        foreach (var message in conversation)
        {
            if (message is SystemChatMessage)
            {
                continue;
            }

            var role = RoleLabel(message);
            if (string.IsNullOrWhiteSpace(role))
            {
                continue;
            }

            var contentParts = BuildResponsesContentParts(message);
            if (contentParts.Count == 0)
            {
                continue;
            }

            input.Add(new ResponsesInputItem(role, contentParts));
        }

        if (input.Count == 0)
        {
            return
            [
                new ResponsesInputItem(
                    "user",
                    [new ResponsesInputContentPart("input_text", "Say hello.")])
            ];
        }

        return input;
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

    private static IReadOnlyList<ResponsesInputContentPart> BuildResponsesContentParts(ChatMessage message)
    {
        var parts = new List<ResponsesInputContentPart>();

        foreach (var part in message.Content)
        {
            if (part.Kind == ChatMessageContentPartKind.Text && !string.IsNullOrWhiteSpace(part.Text))
            {
                parts.Add(new ResponsesInputContentPart("input_text", part.Text.Trim()));
                continue;
            }

            if (part.Kind != ChatMessageContentPartKind.Image)
            {
                continue;
            }

            var imageUrl = part.ImageUri?.ToString();
            if (string.IsNullOrWhiteSpace(imageUrl) && part.ImageBytes is not null)
            {
                var mediaType = string.IsNullOrWhiteSpace(part.ImageBytesMediaType)
                    ? "image/png"
                    : part.ImageBytesMediaType;
                imageUrl = $"data:{mediaType};base64,{Convert.ToBase64String(part.ImageBytes.ToArray())}";
            }

            if (!string.IsNullOrWhiteSpace(imageUrl))
            {
                parts.Add(new ResponsesInputContentPart(
                    "input_image",
                    ImageUrl: imageUrl,
                    Detail: ToResponsesImageDetail(part.ImageDetailLevel)));
            }
        }

        return parts;
    }

    private static string? ToResponsesImageDetail(ChatImageDetailLevel? detailLevel)
    {
        if (detailLevel is null)
        {
            return null;
        }

        if (detailLevel == ChatImageDetailLevel.Low)
        {
            return "low";
        }

        if (detailLevel == ChatImageDetailLevel.High)
        {
            return "high";
        }

        return null;
    }

    private static string RoleLabel(ChatMessage message)
    {
        return message switch
        {
            SystemChatMessage => "system",
            UserChatMessage => "user",
            AssistantChatMessage => "assistant",
            ToolChatMessage => "tool",
            _ => string.Empty
        };
    }

    private static IReadOnlyList<ResponsesToolDefinition>? BuildResponsesTools(IReadOnlyList<ToolDefinition>? tools)
    {
        if (tools is null || tools.Count == 0)
        {
            return null;
        }

        var definitions = new List<ResponsesToolDefinition>(tools.Count);
        foreach (var tool in tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name) || tool.Parameters is null)
            {
                continue;
            }

            definitions.Add(new ResponsesToolDefinition(
                "function",
                tool.Name,
                tool.Description,
                tool.Parameters,
                tool.Strict));
        }

        return definitions.Count == 0 ? null : definitions;
    }

    private static ChatCompletionResult? TryExtractResponseResult(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;

        var toolCalls = TryExtractToolCalls(root);
        if (toolCalls.Count > 0)
        {
            return new ToolCallResult(toolCalls);
        }

        var output = TryExtractOutputText(root);
        if (!string.IsNullOrWhiteSpace(output))
        {
            return new TextResult(output);
        }

        return null;
    }

    private static IReadOnlyList<ToolCall> TryExtractToolCalls(JsonElement root)
    {
        if (!root.TryGetProperty("output", out var outputArray) || outputArray.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var toolCalls = new List<ToolCall>();
        foreach (var outputItem in outputArray.EnumerateArray())
        {
            if (!outputItem.TryGetProperty("type", out var itemType) ||
                !string.Equals(itemType.GetString(), "function_call", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryGetString(outputItem, "name", out var name) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var toolCallId = TryGetString(outputItem, "call_id", out var callId) && !string.IsNullOrWhiteSpace(callId)
                ? callId
                : TryGetString(outputItem, "id", out var id) && !string.IsNullOrWhiteSpace(id)
                    ? id
                    : Guid.NewGuid().ToString("N");

            string argumentsJson = "{}";
            if (outputItem.TryGetProperty("arguments", out var argumentsProperty))
            {
                argumentsJson = argumentsProperty.ValueKind == JsonValueKind.String
                    ? argumentsProperty.GetString() ?? "{}"
                    : argumentsProperty.GetRawText();
            }

            toolCalls.Add(new ToolCall(toolCallId!, name!, argumentsJson));
        }

        return toolCalls;
    }

    private static string TryExtractOutputText(JsonElement root)
    {
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

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        value = property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.GetRawText();
        return true;
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
        IReadOnlyList<ResponsesInputItem> Input,
        string? Instructions,
        IReadOnlyList<ResponsesToolDefinition>? Tools = null);

    private sealed record ResponsesInputItem(
        string Role,
        IReadOnlyList<ResponsesInputContentPart> Content);

    private sealed record ResponsesInputContentPart(
        string Type,
        string? Text = null,
        string? ImageUrl = null,
        string? Detail = null);

    private sealed record ResponsesToolDefinition(
        string Type,
        string Name,
        string? Description,
        object Parameters,
        bool? Strict = null);
}
