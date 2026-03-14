using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReachTether.Server.Services;

public sealed class MemoryPromotionService(
    ISessionStore sessionStore,
    IMemoryEmbeddingProvider embeddingProvider,
    ILogger<MemoryPromotionService> logger) : IMemoryPromotionService
{
    private static readonly Regex PreferencePattern = new(@"(?i)\b(i prefer|my favorite|remember that|always|usually|every|my name is|call me)\b", RegexOptions.Compiled);
    private static readonly Regex OutcomePattern = new(@"(?i)\b(done|completed|scheduled|saved|updated|restored|archived)\b", RegexOptions.Compiled);

    public async Task ProcessPersistedTurnAsync(PersistSessionTurnRequest request, CancellationToken cancellationToken)
    {
        var candidates = new List<PromoteMemoryRequest>();

        if (!string.IsNullOrWhiteSpace(request.UserText) && ShouldPromoteUserText(request.UserText!))
        {
            var title = BuildTitleFromText("User preference", request.UserText!);
            candidates.Add(new PromoteMemoryRequest(
                request.SessionId,
                "session",
                "user_preference",
                title,
                request.UserText!,
                Summarize(request.UserText!),
                request.TurnId,
                0.8));
        }

        if (!string.IsNullOrWhiteSpace(request.AssistantText) && ShouldPromoteAssistantText(request.AssistantText!))
        {
            var title = BuildTitleFromText("Important outcome", request.AssistantText!);
            candidates.Add(new PromoteMemoryRequest(
                request.SessionId,
                "session",
                "important_outcome",
                title,
                request.AssistantText!,
                Summarize(request.AssistantText!),
                request.TurnId,
                0.7));
        }

        if (request.ToolCalls is not null)
        {
            foreach (var toolCall in request.ToolCalls.Where(static call => string.Equals(call.Status, "succeeded", StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(new PromoteMemoryRequest(
                    request.SessionId,
                    "session",
                    "tool_fact",
                    $"Tool result: {toolCall.ToolName}",
                    toolCall.OutputJson ?? "{}",
                    SummarizeToolOutput(toolCall.ToolName, toolCall.OutputJson),
                    request.TurnId,
                    0.65));
            }
        }

        foreach (var candidate in candidates)
        {
            await PromoteAsync(candidate, cancellationToken);
        }

        await UpsertSessionSummaryIfNeededAsync(request, cancellationToken);
    }

    public async Task<PromoteMemoryResponse> PromoteAsync(PromoteMemoryRequest request, CancellationToken cancellationToken)
    {
        var promoted = await sessionStore.UpsertMemoryAsync(request, existingMemoryId: null, cancellationToken);
        var embeddingInput = string.IsNullOrWhiteSpace(request.Summary)
            ? $"{request.Title}\n{request.Content}"
            : $"{request.Title}\n{request.Summary}\n{request.Content}";
        await TryStoreEmbeddingAsync(promoted.MemoryId, embeddingInput, request.Kind, cancellationToken);
        return promoted;
    }

    public async Task<ReindexMemoryResponse> ReindexAsync(ReindexMemoryRequest request, CancellationToken cancellationToken)
    {
        var records = await sessionStore.GetMemoryRecordsForReindexAsync(request.SessionId, request.MemoryIds, cancellationToken);
        var updated = 0;
        foreach (var record in records)
        {
            var input = string.IsNullOrWhiteSpace(record.Summary)
                ? $"{record.Title}\n{record.Content}"
                : $"{record.Title}\n{record.Summary}\n{record.Content}";
            if (await TryStoreEmbeddingAsync(record.MemoryId, input, record.Kind, cancellationToken))
            {
                updated++;
            }
        }

        return new ReindexMemoryResponse(records.Count, updated);
    }

    private async Task UpsertSessionSummaryIfNeededAsync(PersistSessionTurnRequest request, CancellationToken cancellationToken)
    {
        var recentTurns = await sessionStore.GetRecentTurnsAsync(request.SessionId, 8, cancellationToken);
        if (recentTurns.Count < 8)
        {
            return;
        }

        var summaryBuilder = new StringBuilder();
        foreach (var turn in recentTurns)
        {
            summaryBuilder.Append(turn.Role);
            summaryBuilder.Append(": ");
            summaryBuilder.AppendLine(Summarize(turn.Text, 120));
        }

        var promoteRequest = new PromoteMemoryRequest(
            request.SessionId,
            "session",
            "session_summary",
            "Current session summary",
            summaryBuilder.ToString().Trim(),
            Summarize(summaryBuilder.ToString(), 280),
            request.TurnId,
            0.95);

        var promoted = await sessionStore.UpsertMemoryAsync(promoteRequest, await FindExistingSessionSummaryIdAsync(request.SessionId, cancellationToken), cancellationToken);
        await TryStoreEmbeddingAsync(promoted.MemoryId, promoteRequest.Content, promoteRequest.Kind, cancellationToken);
    }

    private async Task<string?> FindExistingSessionSummaryIdAsync(string sessionId, CancellationToken cancellationToken)
    {
        var summary = await sessionStore.GetSessionSummaryAsync(sessionId, cancellationToken);
        return summary?.MemoryId;
    }

    internal static bool ShouldPromoteUserText(string text)
        => PreferencePattern.IsMatch(text) && text.Length >= 12;

    internal static bool ShouldPromoteAssistantText(string text)
        => OutcomePattern.IsMatch(text) && text.Length >= 12;

    internal static string Summarize(string text, int maxLength = 180)
    {
        var singleLine = Regex.Replace(text, @"\s+", " ").Trim();
        return singleLine.Length <= maxLength ? singleLine : $"{singleLine[..(maxLength - 3)]}...";
    }

    private static string BuildTitleFromText(string prefix, string text)
        => $"{prefix}: {Summarize(text, 48)}";

    private static string SummarizeToolOutput(string toolName, string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson))
        {
            return $"Successful {toolName} tool execution.";
        }

        try
        {
            using var document = JsonDocument.Parse(outputJson);
            return $"{toolName}: {Summarize(document.RootElement.ToString(), 180)}";
        }
        catch
        {
            return $"{toolName}: {Summarize(outputJson, 180)}";
        }
    }

    private async Task<bool> TryStoreEmbeddingAsync(
        string memoryId,
        string input,
        string kind,
        CancellationToken cancellationToken)
    {
        try
        {
            var embedding = await embeddingProvider.EmbedAsync(new EmbeddingRequest(input, kind), cancellationToken);
            await sessionStore.UpsertMemoryVectorAsync(memoryId, embedding, cancellationToken);
            return true;
        }
        catch (Exception ex) when (IsEmbeddingUnavailable(ex))
        {
            logger.LogWarning(ex, "Skipping embedding storage for memory record {MemoryId} because no embedding provider is available.", memoryId);
            return false;
        }
    }

    private static bool IsEmbeddingUnavailable(Exception ex)
        => ex is InvalidOperationException or HttpRequestException or NotSupportedException;
}
