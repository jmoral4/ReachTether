namespace ReachTether.Server.Services;

public sealed class MemoryRetrievalService(
    ISessionStore sessionStore,
    IMemoryEmbeddingProvider embeddingProvider,
    ILogger<MemoryRetrievalService> logger) : IMemoryRetrievalService
{
    public async Task<KnowledgeQueryResponse> QueryAsync(KnowledgeQueryRequest request, CancellationToken cancellationToken)
    {
        var topK = request.TopK.GetValueOrDefault(4);
        var ftsMatches = await sessionStore.SearchMemoryByTextAsync(request.SessionId, request.Query, topK, cancellationToken);
        var allActive = await sessionStore.GetActiveMemoryRecordsAsync(request.SessionId, cancellationToken);
        var vectorMatches = Array.Empty<(StoredMemoryRecord Memory, double Score)>();

        try
        {
            var embedding = await embeddingProvider.EmbedAsync(new EmbeddingRequest(request.Query, "knowledge_query"), cancellationToken);
            vectorMatches = allActive
                .Where(static item => item.Embedding is { Count: > 0 })
                .Select(item => (Memory: item, Score: CosineSimilarity(item.Embedding!, embedding.Embedding)))
                .OrderByDescending(static item => item.Score)
                .Take(topK)
                .ToArray();
        }
        catch (Exception ex) when (IsEmbeddingUnavailable(ex))
        {
            logger.LogWarning(ex, "Memory retrieval is falling back to FTS-only search because embeddings are unavailable.");
        }

        var fused = FuseResults(ftsMatches, vectorMatches, topK);
        return new KnowledgeQueryResponse(
            fused,
            await sessionStore.GetActiveProfileAsync(request.SessionId, cancellationToken),
            await sessionStore.GetSessionSummaryAsync(request.SessionId, cancellationToken),
            await sessionStore.GetPendingSystemEventsAsync(request.SessionId, cancellationToken));
    }

    public IReadOnlyList<RetrievedMemoryItem> FuseResults(
        IReadOnlyList<StoredMemoryRecord> ftsMatches,
        IReadOnlyList<(StoredMemoryRecord Memory, double Score)> vectorMatches,
        int topK)
    {
        var merged = new Dictionary<string, (StoredMemoryRecord Memory, double Score)>(StringComparer.OrdinalIgnoreCase);

        foreach (var match in vectorMatches)
        {
            var score = 0.65 * match.Score;
            if (ContainsExactTextHint(match.Memory))
            {
                score += 0.1;
            }

            merged[match.Memory.MemoryId] = (match.Memory, score);
        }

        foreach (var match in ftsMatches)
        {
            var score = (merged.TryGetValue(match.MemoryId, out var existing) ? existing.Score : 0d) + (0.35 * match.TextScore);
            if (ContainsExactTextHint(match))
            {
                score += 0.1;
            }

            merged[match.MemoryId] = (match, score);
        }

        return merged.Values
            .OrderByDescending(static item => item.Score)
            .ThenByDescending(static item => item.Memory.Importance)
            .Take(topK)
            .Select(static item => new RetrievedMemoryItem(
                item.Memory.MemoryId,
                item.Memory.Title,
                item.Memory.Summary ?? MemoryPromotionService.Summarize(item.Memory.Content, 180),
                item.Memory.Kind,
                item.Memory.ProfileId is not null ? "profile" : item.Memory.Scope,
                item.Memory.SourceTurnId,
                Math.Round(item.Score, 4)))
            .ToArray();
    }

    private static double CosineSimilarity(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        if (a.Count == 0 || b.Count == 0 || a.Count != b.Count)
        {
            return 0;
        }

        double dot = 0;
        double aNorm = 0;
        double bNorm = 0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += a[i] * b[i];
            aNorm += a[i] * a[i];
            bNorm += b[i] * b[i];
        }

        if (aNorm == 0 || bNorm == 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(aNorm) * Math.Sqrt(bNorm));
    }

    private static bool ContainsExactTextHint(StoredMemoryRecord record)
        => record.Title.Contains(":", StringComparison.Ordinal) || record.Content.Length < 220;

    private static bool IsEmbeddingUnavailable(Exception ex)
        => ex is InvalidOperationException or HttpRequestException or NotSupportedException;
}
