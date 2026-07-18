using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

internal sealed class OpenAiHeadDetector(
    OpenAiResponsesClient responsesClient,
    RobotAppOptions options,
    ILogger<OpenAiHeadDetector> logger) : IHeadDetector
{
    private const string DetectionPrompt =
        """
        Analyze this robot camera frame for head tracking.
        If no clear human face is present, return exactly:
        {"hasTarget":false}

        Otherwise choose one face to track, preferring the most prominent visible face.
        Return exactly one JSON object:
        {
          "hasTarget": true,
          "centerX": number,
          "centerY": number,
          "confidence": number,
          "relativeSize": number
        }

        Rules:
        - centerX and centerY are normalized to [-1, 1]
        - (-1, -1) is top-left
        - (1, 1) is bottom-right
        - confidence is 0..1
        - relativeSize is 0..1 and should roughly match visible face area prominence

        Output JSON only.
        """;

    public async Task<DetectionResult?> DetectAsync(
        VideoFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var imageUrl = $"data:{frame.MediaType};base64,{Convert.ToBase64String(frame.ImageBytes)}";
        var model = string.IsNullOrWhiteSpace(options.Vision.FaceTrackingModel)
            ? "gpt-4o-mini"
            : options.Vision.FaceTrackingModel;

        var payload = new ResponsesRequest(
            model,
            [
                new ResponsesInputItem(
                    "message",
                    "user",
                    [
                        new ResponsesInputContentPart("input_text", Text: DetectionPrompt),
                        new ResponsesInputContentPart("input_image", ImageUrl: imageUrl, Detail: "low")
                    ])
            ]);

        using var response = await responsesClient.HttpClient.PostAsJsonAsync("responses", payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Head detection request failed (status={(int)response.StatusCode}): {body}");
        }

        using var document = JsonDocument.Parse(body);
        var outputText = TryExtractOutputText(document.RootElement);
        if (string.IsNullOrWhiteSpace(outputText))
        {
            throw new InvalidOperationException("Head detection returned no text.");
        }

        var jsonText = ExtractJsonObject(outputText);
        var result = JsonSerializer.Deserialize<DetectorResponse>(jsonText, SerializerOptions)
            ?? throw new InvalidOperationException("Head detection returned unreadable JSON.");
        if (!result.HasTarget)
        {
            return null;
        }

        var detection = new DetectionResult(
            ClampNormalized(result.CenterX),
            ClampNormalized(result.CenterY),
            ClampUnit(result.Confidence),
            ClampUnit(result.RelativeSize),
            frame.TimestampUtc);
        logger.LogDebug(
            "Head target detected: centerX={CenterX:F3}, centerY={CenterY:F3}, confidence={Confidence:F3}, size={RelativeSize:F3}",
            detection.CenterX,
            detection.CenterY,
            detection.Confidence,
            detection.AreaNormalized);
        return detection;
    }

    private static string TryExtractOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var outputTextProperty)
            && outputTextProperty.ValueKind == JsonValueKind.String)
        {
            return outputTextProperty.GetString()?.Trim() ?? string.Empty;
        }

        if (!root.TryGetProperty("output", out var outputArray) || outputArray.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var outputItem in outputArray.EnumerateArray())
        {
            if (!outputItem.TryGetProperty("type", out var itemType)
                || !string.Equals(itemType.GetString(), "message", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!outputItem.TryGetProperty("content", out var contentArray) || contentArray.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var contentItem in contentArray.EnumerateArray())
            {
                if (!contentItem.TryGetProperty("type", out var contentType)
                    || !string.Equals(contentType.GetString(), "output_text", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (contentItem.TryGetProperty("text", out var textProperty) && textProperty.ValueKind == JsonValueKind.String)
                {
                    return textProperty.GetString()?.Trim() ?? string.Empty;
                }
            }
        }

        return string.Empty;
    }

    private static string ExtractJsonObject(string outputText)
    {
        var trimmed = outputText.Trim();
        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace < 0 || lastBrace <= firstBrace)
        {
            throw new InvalidOperationException($"Head detection did not return a JSON object: {outputText}");
        }

        return trimmed[firstBrace..(lastBrace + 1)];
    }

    private static double ClampNormalized(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0.0;
        }

        return Math.Clamp(value, -1.0, 1.0);
    }

    private static double ClampUnit(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0.0;
        }

        return Math.Clamp(value, 0.0, 1.0);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record ResponsesRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] IReadOnlyList<ResponsesInputItem> Input);

    private sealed record ResponsesInputItem(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] IReadOnlyList<ResponsesInputContentPart> Content);

    private sealed record ResponsesInputContentPart(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
        [property: JsonPropertyName("image_url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ImageUrl = null,
        [property: JsonPropertyName("detail"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail = null);

    private sealed class DetectorResponse
    {
        [JsonPropertyName("hasTarget")]
        public bool HasTarget { get; init; }

        [JsonPropertyName("centerX")]
        public double CenterX { get; init; }

        [JsonPropertyName("centerY")]
        public double CenterY { get; init; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; init; }

        [JsonPropertyName("relativeSize")]
        public double RelativeSize { get; init; }
    }
}
