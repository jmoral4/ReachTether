internal static class ToolPromptAugmenter
{
    private const string CameraToolGuidance = """
### TOOL AWARENESS
- A camera tool is available: `camera(question)`.
- Use it for requests that depend on the current visual scene, such as clothing, colors, objects, text in view, or what someone is doing.
- Do not guess about live surroundings or appearance when the camera tool can verify them.
- After using the camera tool, answer from the captured image only.
""";

    public static string BuildSystemPrompt(string baseInstructions, bool visionEnabled)
    {
        var prompt = baseInstructions?.Trim() ?? string.Empty;
        if (!visionEnabled)
        {
            return prompt;
        }

        if (prompt.Contains("camera tool", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("use the camera", StringComparison.OrdinalIgnoreCase))
        {
            return prompt;
        }

        return string.IsNullOrWhiteSpace(prompt)
            ? CameraToolGuidance
            : $"{prompt}\n\n{CameraToolGuidance}";
    }
}
