using Microsoft.Extensions.Logging.Abstractions;
using ReachTether.Server.Services;

namespace ReachTether.Server.Tests;

public sealed class MemoryPromotionTests
{
    [Fact]
    public async Task Extraction_CreatesProfileAndPromotesStableFacts()
    {
        var store = await TestHelpers.CreateInitializedStoreAsync();
        var session = await store.StartOrResumeSessionAsync(new StartOrResumeSessionRequest("profile-key", "user", "lane", "default", null), CancellationToken.None);
        var service = new MemoryPromotionService(
            store,
            new FakeMemoryEmbeddingProvider(_ => new EmbeddingVectorResult("test", "test", 2, [1f, 0f])),
            new FakeUserFactExtractionService(_ => new UserFactExtractionResult(
                [
                    new ExtractedFact("identity", "preferred_name", "Jonathan", "jonathan", "stable", 0.93, "My name is Jonathan.", null, "profile"),
                    new ExtractedFact("location", "home_city", "Boston", "boston", "stable", 0.88, "I live in Boston.", null, "profile")
                ],
                "Jonathan lives in Boston.")),
            NullLogger<MemoryPromotionService>.Instance);

        await service.ProcessPersistedTurnAsync(
            new PersistSessionTurnRequest(session.SessionId, "turn-1", "My name is Jonathan. I live in Boston.", "Nice to meet you.", "legacy_chat", "gpt-test", "corr", "default", null, null),
            CancellationToken.None);

        var activeProfile = await store.GetActiveProfileAsync(session.SessionId, CancellationToken.None);
        var hits = await store.SearchMemoryByTextAsync(session.SessionId, "Boston", 10, CancellationToken.None);

        Assert.NotNull(activeProfile);
        Assert.Equal("Jonathan", activeProfile!.DisplayName);
        Assert.Contains(hits, static hit => hit.ProfileId is not null && hit.AttributeName == "home_city");
    }

    [Fact]
    public async Task AmbiguousName_CreatesPendingClarificationEvent()
    {
        var store = await TestHelpers.CreateInitializedStoreAsync();
        await store.CreateProfileAsync("Jonathan", "jonathan", CancellationToken.None);
        await store.CreateProfileAsync("Jonathan", "jonathan", CancellationToken.None);
        var session = await store.StartOrResumeSessionAsync(new StartOrResumeSessionRequest("amb-key", "user", "lane", "default", null), CancellationToken.None);
        var service = new MemoryPromotionService(
            store,
            new FakeMemoryEmbeddingProvider(_ => new EmbeddingVectorResult("test", "test", 2, [1f, 0f])),
            new FakeUserFactExtractionService(_ => new UserFactExtractionResult(
                [new ExtractedFact("identity", "preferred_name", "Jonathan", "jonathan", "stable", 0.95, "I'm Jonathan.", null, "profile")],
                null)),
            NullLogger<MemoryPromotionService>.Instance);

        await service.ProcessPersistedTurnAsync(
            new PersistSessionTurnRequest(session.SessionId, "turn-1", "I'm Jonathan.", null, "legacy_chat", "gpt-test", "corr", "default", null, null),
            CancellationToken.None);

        var activeProfile = await store.GetActiveProfileAsync(session.SessionId, CancellationToken.None);
        var events = await store.GetPendingSystemEventsAsync(session.SessionId, CancellationToken.None);

        Assert.Null(activeProfile);
        Assert.Contains(events, static item => item.Title.Contains("clarification", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConflictingProfileFact_RaisesEventInsteadOfOverwriting()
    {
        var store = await TestHelpers.CreateInitializedStoreAsync();
        var session = await store.StartOrResumeSessionAsync(new StartOrResumeSessionRequest("conflict-key", "user", "lane", "default", null), CancellationToken.None);
        var profile = await store.CreateProfileAsync("Jonathan", "jonathan", CancellationToken.None);
        await store.LinkSessionToProfileAsync(session.SessionId, profile.ProfileId, CancellationToken.None);
        await store.UpsertMemoryAsync(
            new PromoteMemoryRequest(session.SessionId, "profile", profile.ProfileId, "work", "employer", "employer: OpenAI", "I work at OpenAI.", "employer = OpenAI", "openai", "turn-0", 0.9),
            null,
            CancellationToken.None);

        var service = new MemoryPromotionService(
            store,
            new FakeMemoryEmbeddingProvider(_ => new EmbeddingVectorResult("test", "test", 2, [1f, 0f])),
            new FakeUserFactExtractionService(_ => new UserFactExtractionResult(
                [new ExtractedFact("work", "employer", "Contoso", "contoso", "stable", 0.91, "I work at Contoso.", null, "profile")],
                null)),
            NullLogger<MemoryPromotionService>.Instance);

        await service.ProcessPersistedTurnAsync(
            new PersistSessionTurnRequest(session.SessionId, "turn-1", "I work at Contoso.", null, "legacy_chat", "gpt-test", "corr", "default", null, null),
            CancellationToken.None);

        var events = await store.GetPendingSystemEventsAsync(session.SessionId, CancellationToken.None);
        var profileFacts = await store.GetProfileMemoryRecordsAsync(profile.ProfileId, CancellationToken.None);

        Assert.Contains(events, static item => item.Title.Contains("confirmation", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profileFacts, static item => item.NormalizedValue == "contoso");
    }
}
