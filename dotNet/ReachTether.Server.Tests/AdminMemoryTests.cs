using ReachTether.Server.Services;

namespace ReachTether.Server.Tests;

public sealed class AdminMemoryTests
{
    [Fact]
    public async Task ArchiveAndRestore_MemoryWithoutDataLoss()
    {
        var store = await TestHelpers.CreateInitializedStoreAsync();
        var session = await store.StartOrResumeSessionAsync(new StartOrResumeSessionRequest("admin-key", "user", "lane", "default", null), CancellationToken.None);
        var promoted = await store.UpsertMemoryAsync(
            new PromoteMemoryRequest(session.SessionId, "session", "tool_fact", "Robot note", "Robot remembers the hallway.", "hallway", "turn-1", 0.8),
            null,
            CancellationToken.None);

        await store.ArchiveMemoryAsync(promoted.MemoryId, CancellationToken.None);
        var hidden = await store.SearchMemoryForAdminAsync(session.SessionId, "hallway", includeArchived: false, 10, CancellationToken.None);
        Assert.Empty(hidden);

        await store.RestoreMemoryAsync(promoted.MemoryId, CancellationToken.None);
        var restored = await store.SearchMemoryForAdminAsync(session.SessionId, "hallway", includeArchived: false, 10, CancellationToken.None);
        Assert.Contains(restored, static item => item.Title == "Robot note");
    }
}
