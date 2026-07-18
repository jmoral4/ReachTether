internal static class ToolPromptAugmenter
{
    private const string CameraToolGuidance = """
### TOOL AWARENESS
- A camera tool is available: `camera(question)`.
- Use it for requests that depend on the current visual scene, such as clothing, colors, objects, text in view, or what someone is doing.
- Do not guess about live surroundings or appearance when the camera tool can verify them.
- After using the camera tool, answer from the captured image only.
""";

    private const string FaceTrackingGuidance = """
### FACE TRACKING
- Automatic face tracking is enabled in the runtime.
- You do not need to call a separate face-tracking tool.
- When a person is visibly present, the robot may automatically orient toward the most prominent visible face or likely speaker.
- If asked whether face tracking exists, answer that it is automatic when enabled in configuration.
""";

    public static string BuildSystemPrompt(string baseInstructions, RobotAppOptions.VisionSettings vision)
    {
        var prompt = baseInstructions?.Trim() ?? string.Empty;
        if (!vision.Enabled)
        {
            return prompt;
        }

        if (prompt.Contains("camera tool", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("use the camera", StringComparison.OrdinalIgnoreCase))
        {
            return vision.FaceTrackingEnabled && !prompt.Contains("face tracking", StringComparison.OrdinalIgnoreCase)
                ? $"{prompt}\n\n{FaceTrackingGuidance}"
                : prompt;
        }

        var guidance = vision.FaceTrackingEnabled
            ? $"{CameraToolGuidance}\n\n{FaceTrackingGuidance}"
            : CameraToolGuidance;

        return string.IsNullOrWhiteSpace(prompt)
            ? guidance
            : $"{prompt}\n\n{guidance}";
    }
}
