using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Audio;
using OpenAI.Chat;
using ReachTether.Audio;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed record TranscriptionCaptureResult(
    string? Text,
    string Stage,
    string? FailureReason,
    int FrameCount,
    long PcmBytes);

internal sealed record AudioClients(
    AudioClient Transcription,
    AudioClient Speech);

internal abstract record ChatCompletionResult(string? ResponseId);

internal sealed record TextResult(string Text, string? ResponseId = null) : ChatCompletionResult(ResponseId);

internal sealed record ToolCall(
    string Id,
    string Name,
    string ArgumentsJson);

internal sealed record ToolCallResult(
    IReadOnlyList<ToolCall> ToolCalls,
    string? ResponseId = null) : ChatCompletionResult(ResponseId);

internal sealed record ToolCallOutput(
    string CallId,
    string OutputJson);

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

    Task<ChatCompletionResult> ContinueToolCallsAsync(
        string previousResponseId,
        IReadOnlyList<ToolCallOutput> toolOutputs,
        IReadOnlyList<ChatMessage>? supplementalConversation = null,
        IReadOnlyList<ToolDefinition>? tools = null,
        string? instructions = null,
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

    public async Task<ChatCompletionResult> ContinueToolCallsAsync(
        string previousResponseId,
        IReadOnlyList<ToolCallOutput> toolOutputs,
        IReadOnlyList<ChatMessage>? supplementalConversation = null,
        IReadOnlyList<ToolDefinition>? tools = null,
        string? instructions = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(previousResponseId))
        {
            throw new ArgumentException("A previous response id is required for tool continuation.", nameof(previousResponseId));
        }

        var input = BuildToolContinuationInput(toolOutputs, supplementalConversation);

        try
        {
            return await CompleteWithResponsesApiAsync(
                appOptions.ChatModel,
                input,
                instructions,
                tools,
                cancellationToken,
                previousResponseId);
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
                        "Responses API tool continuation failed for primary model '{PrimaryModel}'. Retrying with fallback model '{FallbackModel}'.",
                        appOptions.ChatModel,
                        appOptions.ChatFallbackModel);

                    return await CompleteWithResponsesApiAsync(
                        appOptions.ChatFallbackModel,
                        input,
                        instructions,
                        tools,
                        cancellationToken,
                        previousResponseId);
                }
                catch (Exception fallbackEx)
                {
                    logger.LogError(
                        fallbackEx,
                        "Responses API tool continuation failed for fallback model '{FallbackModel}' after primary model '{PrimaryModel}' failed.",
                        appOptions.ChatFallbackModel,
                        appOptions.ChatModel);
                }
            }
            else
            {
                logger.LogError(
                    primaryEx,
                    "Responses API tool continuation failed for model '{Model}'.",
                    appOptions.ChatModel);
            }
        }

        return new TextResult("I ran into a model error while thinking. Please try again.", previousResponseId);
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
        CancellationToken cancellationToken,
        string? previousResponseId = null)
    {
        var modelHandle = ParseModelHandle(model);
        var payload = new ResponsesRequest(
            modelHandle.Model,
            input,
            instructions,
            BuildResponsesTools(tools),
            previousResponseId,
            modelHandle.ReasoningEffort is null
                ? null
                : new ResponsesReasoning(modelHandle.ReasoningEffort));

        logger.LogInformation(
            "Submitting Responses API request: model={Model}, reasoningEffort={ReasoningEffort}, previousResponseId={PreviousResponseId}, inputItems={InputItems}, instructionsChars={InstructionsChars}, tools={ToolCount}, inputSummary={InputSummary}",
            modelHandle.Model,
            modelHandle.ReasoningEffort ?? "<default>",
            previousResponseId ?? "<none>",
            input.Count,
            instructions?.Length ?? 0,
            tools?.Count ?? 0,
            SummarizeResponsesInput(input));

        if (appOptions.Diagnostics.LogResponsesApiBodies)
        {
            logger.LogDebug(
                "Responses API request body: {Payload}",
                CompactJsonForLogs(JsonSerializer.Serialize(payload), appOptions.Diagnostics.ResponsesApiBodyMaxChars));
        }

        using var response = await responsesClient.HttpClient.PostAsJsonAsync("responses", payload, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (appOptions.Diagnostics.LogResponsesApiBodies)
        {
            logger.LogDebug(
                "Responses API response body: {Payload}",
                CompactJsonForLogs(responseBody, appOptions.Diagnostics.ResponsesApiBodyMaxChars));
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Responses API request failed: model={Model}, status={StatusCode}, inputSummary={InputSummary}, error={Error}",
                model,
                (int)response.StatusCode,
                SummarizeResponsesInput(input),
                ExtractResponsesErrorMessage(responseBody));
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

    private static IReadOnlyList<ResponsesInputItem> BuildToolContinuationInput(
        IReadOnlyList<ToolCallOutput> toolOutputs,
        IReadOnlyList<ChatMessage>? supplementalConversation)
    {
        var input = new List<ResponsesInputItem>(toolOutputs.Count + (supplementalConversation?.Count ?? 0));

        foreach (var output in toolOutputs)
        {
            if (string.IsNullOrWhiteSpace(output.CallId))
            {
                continue;
            }

            input.Add(new ResponsesInputItem(
                "function_call_output",
                CallId: output.CallId,
                Output: output.OutputJson));
        }

        if (supplementalConversation is not null)
        {
            input.AddRange(BuildResponsesInput(supplementalConversation));
        }

        return input;
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

            var contentParts = BuildResponsesContentParts(message, role);
            if (contentParts.Count == 0)
            {
                continue;
            }

            input.Add(new ResponsesInputItem("message", role, contentParts));
        }

        if (input.Count == 0)
        {
            return
            [
                new ResponsesInputItem(
                    "message",
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

    private static IReadOnlyList<ResponsesInputContentPart> BuildResponsesContentParts(ChatMessage message, string role)
    {
        var parts = new List<ResponsesInputContentPart>();
        var textPartType = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
            ? "output_text"
            : "input_text";

        foreach (var part in message.Content)
        {
            if (part.Kind == ChatMessageContentPartKind.Text && !string.IsNullOrWhiteSpace(part.Text))
            {
                parts.Add(new ResponsesInputContentPart(textPartType, part.Text.Trim()));
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
            return "auto";
        }

        if (detailLevel == ChatImageDetailLevel.Low)
        {
            return "low";
        }

        if (detailLevel == ChatImageDetailLevel.High)
        {
            return "high";
        }

        return "auto";
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
        var responseId = TryGetString(root, "id", out var id) ? id : null;

        var toolCalls = TryExtractToolCalls(root);
        if (toolCalls.Count > 0)
        {
            return new ToolCallResult(toolCalls, responseId);
        }

        var output = TryExtractOutputText(root);
        if (!string.IsNullOrWhiteSpace(output))
        {
            return new TextResult(output, responseId);
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

    private static string CompactJsonForLogs(string value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        string compact;
        try
        {
            using var document = JsonDocument.Parse(value);
            var sanitized = SanitizeElement(document.RootElement);
            compact = JsonSerializer.Serialize(sanitized);
        }
        catch (JsonException)
        {
            compact = value.ReplaceLineEndings(" ").Trim();
        }

        if (compact.Length <= maxChars)
        {
            return compact;
        }

        return $"{compact[..maxChars]}... [truncated {compact.Length - maxChars} chars]";
    }

    private static object? SanitizeElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => SanitizeProperty(property.Name, property.Value),
                    StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(SanitizeElement).ToArray(),
            JsonValueKind.String => SanitizeString(element.GetString()),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }

    private static object? SanitizeProperty(string propertyName, JsonElement value)
    {
        if ((string.Equals(propertyName, "image_url", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyName, "b64_im", StringComparison.OrdinalIgnoreCase))
            && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? string.Empty;
            return $"<omitted:{propertyName}:length={text.Length}>";
        }

        return SanitizeElement(value);
    }

    private static string? SanitizeString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        const string DataUrlMarker = ";base64,";
        var markerIndex = value.IndexOf(DataUrlMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0 && value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return $"{value[..(markerIndex + DataUrlMarker.Length)]}<omitted:{value.Length - (markerIndex + DataUrlMarker.Length)} chars>";
        }

        return value;
    }

    private static string SummarizeResponsesInput(IReadOnlyList<ResponsesInputItem> input)
    {
        if (input.Count == 0)
        {
            return "none";
        }

        return string.Join(
            "; ",
            input.Select(item =>
            {
                if (string.Equals(item.Type, "function_call_output", StringComparison.OrdinalIgnoreCase))
                {
                    return $"function_call_output[{item.CallId}]";
                }

                var contentSummary = string.Join(
                    ",",
                    item.Content?.Select(part => part.Type) ?? []);
                return $"{item.Role}[{contentSummary}]";
            }));
    }

    private static OpenAiModelHandle ParseModelHandle(string configuredModel)
    {
        var separatorIndex = configuredModel.LastIndexOf('@');
        if (separatorIndex <= 0 || separatorIndex == configuredModel.Length - 1)
        {
            return new OpenAiModelHandle(configuredModel, null);
        }

        var effort = configuredModel[(separatorIndex + 1)..];
        if (effort is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max"))
        {
            return new OpenAiModelHandle(configuredModel, null);
        }

        return new OpenAiModelHandle(configuredModel[..separatorIndex], effort);
    }

    private sealed record ResponsesRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] IReadOnlyList<ResponsesInputItem> Input,
        [property: JsonPropertyName("instructions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Instructions,
        [property: JsonPropertyName("tools"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ResponsesToolDefinition>? Tools = null,
        [property: JsonPropertyName("previous_response_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PreviousResponseId = null,
        [property: JsonPropertyName("reasoning"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ResponsesReasoning? Reasoning = null);

    private sealed record OpenAiModelHandle(string Model, string? ReasoningEffort);

    private sealed record ResponsesReasoning(
        [property: JsonPropertyName("effort")] string Effort);

    private sealed record ResponsesInputItem(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("role"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Role = null,
        [property: JsonPropertyName("content"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ResponsesInputContentPart>? Content = null,
        [property: JsonPropertyName("call_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CallId = null,
        [property: JsonPropertyName("output"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Output = null);

    private sealed record ResponsesInputContentPart(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
        [property: JsonPropertyName("image_url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImageUrl = null,
        [property: JsonPropertyName("detail"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail = null);

    private sealed record ResponsesToolDefinition(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description,
        [property: JsonPropertyName("parameters")] object Parameters,
        [property: JsonPropertyName("strict"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Strict = null);
}
