using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReachTether.Server.Services;

public sealed class MemoryPromotionService(
    ISessionStore sessionStore,
    IMemoryEmbeddingProvider embeddingProvider,
    IUserFactExtractionService extractionService,
    ILogger<MemoryPromotionService> logger) : IMemoryPromotionService
{
    private static readonly Regex PreferencePattern = new(@"(?i)\b(i prefer|my favorite|remember that|always|usually|every)\b", RegexOptions.Compiled);
    private static readonly Regex NamePattern = new(@"(?i)\b(my name is|i am|i'm|call me)\s+(?<value>[a-z][a-z'\- ]{1,40})", RegexOptions.Compiled);
    private static readonly Regex LocationPattern = new(@"(?i)\b(i live in|i'm from|i am from|i moved to)\s+(?<value>[a-z][a-z'\- ]{1,50})", RegexOptions.Compiled);
    private static readonly Regex EmployerPattern = new(@"(?i)\b(i work at|i work for|i'm at|i am at)\s+(?<value>[a-z0-9][a-z0-9&.,' \-]{1,60})", RegexOptions.Compiled);
    private static readonly Regex RolePattern = new(@"(?i)\b(i work as|i'm a|i am a)\s+(?<value>[a-z][a-z'\- ]{1,60})", RegexOptions.Compiled);
    private static readonly Regex OutcomePattern = new(@"(?i)\b(done|completed|scheduled|saved|updated|restored|archived)\b", RegexOptions.Compiled);

    public async Task ProcessPersistedTurnAsync(PersistSessionTurnRequest request, CancellationToken cancellationToken)
    {
        var activeProfile = await sessionStore.GetActiveProfileAsync(request.SessionId, cancellationToken);
        var extraction = await TryExtractFactsAsync(request, cancellationToken);
        var facts = extraction?.Facts.Count > 0
            ? extraction.Facts
            : BuildFallbackFacts(request);

        var resolvedProfile = await ResolveProfileAsync(request, facts, activeProfile, cancellationToken);
        foreach (var fact in facts)
        {
            await PromoteExtractedFactAsync(request, fact, resolvedProfile, cancellationToken);
        }

        foreach (var candidate in BuildToolCandidates(request))
        {
            await PromoteAsync(candidate, cancellationToken);
        }

        await UpsertSessionSummaryIfNeededAsync(request, extraction?.SessionSummary, cancellationToken);
        if (resolvedProfile is not null)
        {
            await RefreshProfileSummaryAsync(resolvedProfile, cancellationToken);
        }
    }

    public async Task<PromoteMemoryResponse> PromoteAsync(PromoteMemoryRequest request, CancellationToken cancellationToken)
    {
        var existingMemoryId = await sessionStore.FindExistingMemoryIdAsync(
            request.SessionId,
            request.Scope,
            request.Kind,
            request.AttributeName,
            request.ProfileId,
            cancellationToken);
        var promoted = await sessionStore.UpsertMemoryAsync(request, existingMemoryId, cancellationToken);
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

    internal static bool ShouldPromoteAssistantText(string text)
        => OutcomePattern.IsMatch(text) && text.Length >= 12;

    internal static string Summarize(string text, int maxLength = 180)
    {
        var singleLine = Regex.Replace(text, @"\s+", " ").Trim();
        return singleLine.Length <= maxLength ? singleLine : $"{singleLine[..(maxLength - 3)]}...";
    }

    private async Task<UserFactExtractionResult?> TryExtractFactsAsync(PersistSessionTurnRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await extractionService.ExtractAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or JsonException or NotSupportedException)
        {
            logger.LogWarning(ex, "User fact extraction unavailable for session {SessionId}; falling back to heuristics.", request.SessionId);
            return null;
        }
    }

    private static IReadOnlyList<ExtractedFact> BuildFallbackFacts(PersistSessionTurnRequest request)
    {
        var facts = new List<ExtractedFact>();
        if (!string.IsNullOrWhiteSpace(request.UserText))
        {
            AddRegexFact(facts, request.UserText!, NamePattern, "identity", "preferred_name", "stable", "profile");
            AddRegexFact(facts, request.UserText!, LocationPattern, "location", "home_city", "stable", "profile");
            AddRegexFact(facts, request.UserText!, EmployerPattern, "work", "employer", "stable", "profile");
            AddRegexFact(facts, request.UserText!, RolePattern, "work", "job_title", "stable", "profile");

            if (PreferencePattern.IsMatch(request.UserText!))
            {
                facts.Add(new ExtractedFact(
                    "preference",
                    "general_preference",
                    Summarize(request.UserText!),
                    NormalizeValue(request.UserText!),
                    "stable",
                    0.62,
                    request.UserText!,
                    null,
                    "profile"));
            }
        }

        return facts;
    }

    private static void AddRegexFact(
        ICollection<ExtractedFact> facts,
        string text,
        Regex pattern,
        string kind,
        string attribute,
        string stability,
        string scopeHint)
    {
        var match = pattern.Match(text);
        if (!match.Success)
        {
            return;
        }

        var value = match.Groups["value"].Value.Trim(' ', '.', '!', '?', ',');
        if (value.Length < 2)
        {
            return;
        }

        facts.Add(new ExtractedFact(
            kind,
            attribute,
            value,
            NormalizeValue(value),
            stability,
            0.7,
            text,
            null,
            scopeHint));
    }

    private async Task<StoredProfileRecord?> ResolveProfileAsync(
        PersistSessionTurnRequest request,
        IReadOnlyList<ExtractedFact> facts,
        ActiveProfileDescriptor? activeProfile,
        CancellationToken cancellationToken)
    {
        if (activeProfile is not null)
        {
            return new StoredProfileRecord(
                activeProfile.ProfileId,
                activeProfile.DisplayName,
                NormalizeValue(activeProfile.DisplayName),
                activeProfile.Summary,
                activeProfile.UpdatedAt,
                activeProfile.UpdatedAt);
        }

        var nameFact = facts.FirstOrDefault(static fact =>
            string.Equals(fact.Attribute, "preferred_name", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fact.Attribute, "name", StringComparison.OrdinalIgnoreCase));
        if (nameFact is null || string.IsNullOrWhiteSpace(nameFact.NormalizedValue))
        {
            return null;
        }

        var matches = await sessionStore.FindProfilesByNormalizedNameAsync(nameFact.NormalizedValue, cancellationToken);
        if (matches.Count == 1)
        {
            await sessionStore.LinkSessionToProfileAsync(request.SessionId, matches[0].ProfileId, cancellationToken);
            return matches[0];
        }

        if (matches.Count > 1)
        {
            await sessionStore.UpsertPendingSystemEventAsync(
                request.SessionId,
                "profile_disambiguation",
                "Need profile clarification",
                $"Multiple people may match the name '{nameFact.Value}'. Ask which person is speaking before storing durable profile facts.",
                cancellationToken);
            return null;
        }

        var created = await sessionStore.CreateProfileAsync(nameFact.Value, nameFact.NormalizedValue, cancellationToken);
        await sessionStore.LinkSessionToProfileAsync(request.SessionId, created.ProfileId, cancellationToken);
        return created;
    }

    private async Task PromoteExtractedFactAsync(
        PersistSessionTurnRequest request,
        ExtractedFact fact,
        StoredProfileRecord? profile,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(fact.Value) || fact.Confidence < 0.45)
        {
            return;
        }

        var wantsProfileScope = profile is not null
            && (string.Equals(fact.ScopeHint, "profile", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fact.Stability, "stable", StringComparison.OrdinalIgnoreCase));
        var scope = wantsProfileScope ? "profile" : "session";
        var profileId = wantsProfileScope ? profile!.ProfileId : null;

        if (profileId is not null)
        {
            var existingProfileFacts = await sessionStore.GetProfileMemoryRecordsAsync(profileId, cancellationToken);
            var conflicting = existingProfileFacts.FirstOrDefault(item =>
                string.Equals(item.AttributeName, fact.Attribute, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(item.NormalizedValue)
                && !string.IsNullOrWhiteSpace(fact.NormalizedValue)
                && !string.Equals(item.NormalizedValue, fact.NormalizedValue, StringComparison.OrdinalIgnoreCase));

            if (conflicting is not null)
            {
                await sessionStore.UpsertPendingSystemEventAsync(
                    request.SessionId,
                    $"profile_conflict:{fact.Attribute}",
                    "Profile fact needs confirmation",
                    $"The stored {fact.Attribute.Replace('_', ' ')} conflicts with a new statement ('{conflicting.Content}' vs '{fact.Value}'). Ask for confirmation before updating profile memory.",
                    cancellationToken);
                return;
            }
        }

        var promoteRequest = new PromoteMemoryRequest(
            request.SessionId,
            scope,
            profileId,
            NormalizeKind(fact.Kind),
            fact.Attribute,
            BuildTitle(fact),
            fact.Evidence,
            BuildFactSummary(fact),
            fact.NormalizedValue,
            request.TurnId,
            Math.Clamp(fact.Confidence, 0.5, 0.98));
        await PromoteAsync(promoteRequest, cancellationToken);
    }

    private async Task UpsertSessionSummaryIfNeededAsync(
        PersistSessionTurnRequest request,
        string? extractedSummary,
        CancellationToken cancellationToken)
    {
        var recentTurns = await sessionStore.GetRecentTurnsAsync(request.SessionId, 8, cancellationToken);
        if (recentTurns.Count < 4)
        {
            return;
        }

        var summary = extractedSummary;
        if (string.IsNullOrWhiteSpace(summary))
        {
            try
            {
                summary = await extractionService.SummarizeSessionAsync(recentTurns, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or JsonException or NotSupportedException)
            {
                logger.LogWarning(ex, "Session summarization unavailable for session {SessionId}; using fallback summary.", request.SessionId);
                summary = BuildFallbackSessionSummary(recentTurns);
            }
        }

        var content = string.Join(
            "\n",
            recentTurns.Select(static turn => $"{turn.Role}: {Summarize(turn.Text, 120)}"));
        var promoteRequest = new PromoteMemoryRequest(
            request.SessionId,
            "session",
            null,
            "session_summary",
            null,
            "Current session summary",
            content,
            Summarize(summary!, 280),
            NormalizeValue(summary!),
            request.TurnId,
            0.95);

        var promoted = await sessionStore.UpsertMemoryAsync(
            promoteRequest,
            await FindExistingSessionSummaryIdAsync(request.SessionId, cancellationToken),
            cancellationToken);
        await TryStoreEmbeddingAsync(promoted.MemoryId, $"{promoteRequest.Title}\n{summary}", promoteRequest.Kind, cancellationToken);
    }

    private async Task RefreshProfileSummaryAsync(StoredProfileRecord profile, CancellationToken cancellationToken)
    {
        var facts = await sessionStore.GetProfileMemoryRecordsAsync(profile.ProfileId, cancellationToken);
        if (facts.Count == 0)
        {
            return;
        }

        var summary = string.Join(
            "; ",
            facts
                .Where(static fact => !string.Equals(fact.Kind, "session_summary", StringComparison.OrdinalIgnoreCase))
                .Take(5)
                .Select(static fact => $"{fact.AttributeName?.Replace('_', ' ') ?? fact.Kind}: {Summarize(fact.Summary ?? fact.Content, 70)}"));
        await sessionStore.UpdateProfileSummaryAsync(profile.ProfileId, profile.DisplayName, summary, cancellationToken);
    }

    private static IEnumerable<PromoteMemoryRequest> BuildToolCandidates(PersistSessionTurnRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.AssistantText) && ShouldPromoteAssistantText(request.AssistantText!))
        {
            yield return new PromoteMemoryRequest(
                request.SessionId,
                "session",
                null,
                "important_outcome",
                null,
                BuildTitleFromText("Important outcome", request.AssistantText!),
                request.AssistantText!,
                Summarize(request.AssistantText!),
                NormalizeValue(request.AssistantText!),
                request.TurnId,
                0.7);
        }

        if (request.ToolCalls is null)
        {
            yield break;
        }

        foreach (var toolCall in request.ToolCalls.Where(static call => string.Equals(call.Status, "succeeded", StringComparison.OrdinalIgnoreCase)))
        {
            yield return new PromoteMemoryRequest(
                request.SessionId,
                "session",
                null,
                "tool_fact",
                $"{toolCall.ToolName}_result",
                $"Tool result: {toolCall.ToolName}",
                toolCall.OutputJson ?? "{}",
                SummarizeToolOutput(toolCall.ToolName, toolCall.OutputJson),
                NormalizeValue(toolCall.OutputJson ?? toolCall.ToolName),
                request.TurnId,
                0.65);
        }
    }

    private async Task<string?> FindExistingSessionSummaryIdAsync(string sessionId, CancellationToken cancellationToken)
    {
        var summary = await sessionStore.GetSessionSummaryAsync(sessionId, cancellationToken);
        return summary?.MemoryId;
    }

    private static string BuildFallbackSessionSummary(IReadOnlyList<PromptRecentTurn> recentTurns)
    {
        var builder = new StringBuilder();
        foreach (var turn in recentTurns.TakeLast(6))
        {
            builder.Append(turn.Role);
            builder.Append(": ");
            builder.AppendLine(Summarize(turn.Text, 80));
        }

        return Summarize(builder.ToString(), 280);
    }

    private static string NormalizeKind(string kind)
        => string.IsNullOrWhiteSpace(kind) ? "user_fact" : kind.Trim().ToLowerInvariant().Replace(' ', '_');

    private static string BuildTitle(ExtractedFact fact)
        => $"{fact.Attribute.Replace('_', ' ')}: {Summarize(fact.Value, 48)}";

    private static string BuildFactSummary(ExtractedFact fact)
        => $"{fact.Attribute.Replace('_', ' ')} = {Summarize(fact.Value, 120)}";

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

    private static string NormalizeValue(string text)
        => Regex.Replace(text ?? string.Empty, @"[^a-z0-9]+", " ", RegexOptions.IgnoreCase)
            .Trim()
            .ToLowerInvariant();

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
