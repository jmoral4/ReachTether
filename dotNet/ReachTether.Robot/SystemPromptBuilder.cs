internal static class SystemPromptBuilder
{
    public static string BuildSystemPrompt(string baseInstructions, IToolDefinitionSource toolDefinitionSource)
    {
        var prompt = baseInstructions?.Trim() ?? string.Empty;
        var toolGuidance = toolDefinitionSource.BuildToolUsageGuidance();
        if (string.IsNullOrWhiteSpace(toolGuidance))
        {
            return prompt;
        }

        return string.IsNullOrWhiteSpace(prompt)
            ? toolGuidance
            : $"{prompt}\n\n{toolGuidance}";
    }
}
