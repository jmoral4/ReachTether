using System.Text;

internal interface IPromptContextBuilder
{
    string BuildSystemPrompt(
        string baseInstructions,
        IToolDefinitionSource toolDefinitionSource,
        bool resumed,
        ActiveProfileDescriptor? activeProfile,
        IReadOnlyList<PromptRecentTurn> recentTurns,
        IReadOnlyList<RetrievedMemoryItem> retrievedMemory,
        SessionSummaryDescriptor? sessionSummary,
        IReadOnlyList<PendingSystemEventDescriptor> pendingSystemEvents);
}

internal sealed class PromptContextBuilder : IPromptContextBuilder
{
    public string BuildSystemPrompt(
        string baseInstructions,
        IToolDefinitionSource toolDefinitionSource,
        bool resumed,
        ActiveProfileDescriptor? activeProfile,
        IReadOnlyList<PromptRecentTurn> recentTurns,
        IReadOnlyList<RetrievedMemoryItem> retrievedMemory,
        SessionSummaryDescriptor? sessionSummary,
        IReadOnlyList<PendingSystemEventDescriptor> pendingSystemEvents)
    {
        var sections = new List<string>();
        var basePrompt = baseInstructions?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(basePrompt))
        {
            sections.Add(basePrompt);
        }

        if (resumed)
        {
            sections.Add("You are re-entering an ongoing relationship and conversation. Continue with continuity rather than behaving like a fresh existence.");
        }

        sections.Add("When the user asks what you know about them, answer with a compact factual recap first. Mention uncertainty instead of guessing.");

        if (activeProfile is not null)
        {
            sections.Add($"### Active Profile\n- {activeProfile.DisplayName}: {activeProfile.Summary.Trim()}");
        }

        if (sessionSummary is not null && !string.IsNullOrWhiteSpace(sessionSummary.Summary))
        {
            sections.Add($"### Current Session Summary\n- {sessionSummary.Summary.Trim()}");
        }

        if (retrievedMemory.Count > 0)
        {
            var profileMemory = retrievedMemory
                .Where(static item => string.Equals(item.Scope, "profile", StringComparison.OrdinalIgnoreCase))
                .Take(4)
                .ToArray();
            var sessionMemory = retrievedMemory
                .Where(static item => !string.Equals(item.Scope, "profile", StringComparison.OrdinalIgnoreCase))
                .Take(4)
                .ToArray();

            if (profileMemory.Length > 0)
            {
                var builder = new StringBuilder("### Profile Memory\n");
                foreach (var item in profileMemory)
                {
                    builder.Append("- ");
                    builder.Append(item.Title);
                    builder.Append(": ");
                    builder.AppendLine(item.SummaryOrSnippet);
                }

                sections.Add(builder.ToString().Trim());
            }

            if (sessionMemory.Length > 0)
            {
                var builder = new StringBuilder("### Session Memory\n");
                foreach (var item in sessionMemory)
                {
                    builder.Append("- ");
                    builder.Append(item.Title);
                    builder.Append(": ");
                    builder.AppendLine(item.SummaryOrSnippet);
                }

                sections.Add(builder.ToString().Trim());
            }
        }

        if (recentTurns.Count > 0)
        {
            var builder = new StringBuilder("### Recent Turns\n");
            foreach (var turn in recentTurns.TakeLast(4))
            {
                builder.Append("- ");
                builder.Append(turn.Role);
                builder.Append(": ");
                builder.AppendLine(turn.Text);
            }

            sections.Add(builder.ToString().Trim());
        }

        if (pendingSystemEvents.Count > 0)
        {
            var builder = new StringBuilder("### Pending System Events\n");
            foreach (var item in pendingSystemEvents)
            {
                builder.Append("- ");
                builder.Append(item.Title);
                builder.Append(": ");
                builder.AppendLine(item.Summary);
            }

            sections.Add(builder.ToString().Trim());
        }

        var toolGuidance = toolDefinitionSource.BuildToolUsageGuidance();
        if (!string.IsNullOrWhiteSpace(toolGuidance))
        {
            sections.Add(toolGuidance);
        }

        return string.Join("\n\n", sections.Where(static section => !string.IsNullOrWhiteSpace(section)));
    }
}
