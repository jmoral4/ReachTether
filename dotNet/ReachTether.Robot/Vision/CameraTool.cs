using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

internal sealed record CameraToolExecutionResult(
    string Question,
    VisionCameraSnapshot Snapshot,
    string ToolOutputJson,
    string ImageDataUrl);

internal sealed class CameraTool(
    ICameraSnapshotProvider snapshotProvider,
    IMotionOrchestrator motionOrchestrator,
    ILogger<CameraTool> logger)
{
    public const string Name = "camera";
    private const string Description = "Capture the latest robot camera image for visual questions.";
    private static readonly object ToolParametersSchema = new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            question = new
            {
                type = "string",
                description = "What to look for in the current camera image."
            }
        },
        required = new[] { "question" }
    };

    private static readonly ToolDefinition Tool = new(
        Name,
        Description,
        ToolParametersSchema,
        Strict: true);

    public IReadOnlyList<ToolDefinition> ToolDefinitions { get; } = [Tool];
    public BinaryData RealtimeParametersSchema { get; } = BinaryData.FromObjectAsJson(ToolParametersSchema);
    public string RealtimeDescription => Description;

    public bool IsCameraToolCall(ToolCall toolCall)
    {
        return string.Equals(toolCall.Name, Name, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<CameraToolExecutionResult> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken)
    {
        var question = ExtractQuestion(argumentsJson);

        await using var focusLease = await motionOrchestrator.HoldCameraFocusAsync(cancellationToken);
        var snapshot = await snapshotProvider.CaptureSnapshotAsync(cancellationToken);
        if (snapshot is null)
        {
            throw new InvalidOperationException("Camera capture is disabled or unavailable.");
        }

        var base64 = Convert.ToBase64String(snapshot.ImageBytes);
        var mediaType = string.IsNullOrWhiteSpace(snapshot.MediaType) ? "image/jpeg" : snapshot.MediaType;
        var dataUrl = $"data:{mediaType};base64,{base64}";

        var payload = JsonSerializer.Serialize(new
        {
            b64_im = base64,
            image_data_url = dataUrl,
            media_type = mediaType,
            captured_at = snapshot.CapturedAt,
            question
        });

        logger.LogInformation(
            "Camera tool captured snapshot: bytes={Bytes}, mediaType={MediaType}, capturedAt={CapturedAt}",
            snapshot.ImageBytes.Length,
            mediaType,
            snapshot.CapturedAt);

        return new CameraToolExecutionResult(question, snapshot, payload, dataUrl);
    }

    public UserChatMessage BuildImageAnswerContextMessage(CameraToolExecutionResult execution)
    {
        var text = string.IsNullOrWhiteSpace(execution.Question)
            ? "The camera tool has already returned this image. Answer the user's request from it and do not call the camera again unless a newer view is required."
            : $"The camera tool has already returned this image for the request \"{execution.Question}\". Answer the user's request from it and do not call the camera again unless a newer view is required.";

        return new UserChatMessage(
        [
            ChatMessageContentPart.CreateTextPart(text),
            ChatMessageContentPart.CreateImagePart(
                BinaryData.FromBytes(execution.Snapshot.ImageBytes),
                execution.Snapshot.MediaType,
                null)
        ]);
    }

    public BinaryData BuildRealtimeImageMessageCommand(CameraToolExecutionResult execution)
    {
        var text = string.IsNullOrWhiteSpace(execution.Question)
            ? "Please answer based on this latest camera image."
            : execution.Question;

        return BinaryData.FromObjectAsJson(new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "user",
                content = new object[]
                {
                    new
                    {
                        type = "input_text",
                        text
                    },
                    new
                    {
                        type = "input_image",
                        image_url = execution.ImageDataUrl
                    }
                }
            }
        });
    }

    private static string ExtractQuestion(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return "What do you see in this image?";
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return "What do you see in this image?";
            }

            if (document.RootElement.TryGetProperty("question", out var questionProperty))
            {
                var question = questionProperty.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(question))
                {
                    return question;
                }
            }
        }
        catch (Exception)
        {
            // Fall back to default prompt for malformed tool arguments.
        }

        return "What do you see in this image?";
    }
}
