using ReachTether.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ReachTether.Server.Tests;

public sealed class MemoryRetrievalTests
{
    [Fact]
    public async Task FtsOnlyQuery_ReturnsPromotedMemory()
    {
        var store = await TestHelpers.CreateInitializedStoreAsync();
        var session = await store.StartOrResumeSessionAsync(new StartOrResumeSessionRequest("fts-key", "user", "lane", "default", null), CancellationToken.None);
        await store.UpsertMemoryAsync(new PromoteMemoryRequest(session.SessionId, "session", null, "user_preference", null, "Blue mug preference", "User prefers the blue mug on the left shelf.", "blue mug", null, "turn-1", 0.8), null, CancellationToken.None);
        var retrieval = new MemoryRetrievalService(
            store,
            new FakeMemoryEmbeddingProvider(_ => new EmbeddingVectorResult("test", "test", 3, [0f, 0f, 1f])),
            NullLogger<MemoryRetrievalService>.Instance);

        var result = await retrieval.QueryAsync(new KnowledgeQueryRequest(session.SessionId, "blue mug", 3), CancellationToken.None);

        Assert.Contains(result.Hits, static hit => hit.Title.Contains("Blue mug", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VectorQuery_ReturnsClosestSemanticMatch_AndSkipsArchivedRecords()
    {
        var store = await TestHelpers.CreateInitializedStoreAsync();
        var session = await store.StartOrResumeSessionAsync(new StartOrResumeSessionRequest("vec-key", "user", "lane", "default", null), CancellationToken.None);
        var first = await store.UpsertMemoryAsync(new PromoteMemoryRequest(session.SessionId, "session", null, "tool_fact", null, "Robot battery note", "Battery at 80 percent.", "battery status", null, "turn-1", 0.8), null, CancellationToken.None);
        await store.UpsertMemoryVectorAsync(first.MemoryId, new EmbeddingVectorResult("test", "test", 3, [1f, 0f, 0f]), CancellationToken.None);
        var archived = await store.UpsertMemoryAsync(new PromoteMemoryRequest(session.SessionId, "session", null, "tool_fact", null, "Archived note", "Battery at 95 percent.", "battery old", null, "turn-2", 0.9), null, CancellationToken.None);
        await store.UpsertMemoryVectorAsync(archived.MemoryId, new EmbeddingVectorResult("test", "test", 3, [1f, 0f, 0f]), CancellationToken.None);
        await store.ArchiveMemoryAsync(archived.MemoryId, CancellationToken.None);

        var retrieval = new MemoryRetrievalService(
            store,
            new FakeMemoryEmbeddingProvider(_ => new EmbeddingVectorResult("test", "test", 3, [1f, 0f, 0f])),
            NullLogger<MemoryRetrievalService>.Instance);
        var result = await retrieval.QueryAsync(new KnowledgeQueryRequest(session.SessionId, "power status", 3), CancellationToken.None);

        Assert.Single(result.Hits);
        Assert.Equal(first.MemoryId, result.Hits[0].MemoryId);
    }
}
