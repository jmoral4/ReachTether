using ReachTether.Server.Services;

namespace ReachTether.Server.Tests;

public sealed class SessionStoreTests
{
    [Fact]
    public async Task StartOrResumeSession_ReusesSameSessionId()
    {
        var store = await TestHelpers.CreateInitializedStoreAsync();
        var first = await store.StartOrResumeSessionAsync(new StartOrResumeSessionRequest("key-1", "user-1", "lane-a", "default", null), CancellationToken.None);
        var second = await store.StartOrResumeSessionAsync(new StartOrResumeSessionRequest("key-1", "user-1", "lane-a", "default", null), CancellationToken.None);

        Assert.Equal(first.SessionId, second.SessionId);
        Assert.False(first.Resumed);
        Assert.True(second.Resumed);
    }

    [Fact]
    public async Task PersistedTurns_SurviveStoreRecreation()
    {
        var path = TestHelpers.CreateTempDbPath();
        var store = await TestHelpers.CreateInitializedStoreAsync(path);
        var session = await store.StartOrResumeSessionAsync(new StartOrResumeSessionRequest("key-2", "user-1", "lane-a", "default", null), CancellationToken.None);

        await store.PersistSessionTurnAsync(
            new PersistSessionTurnRequest(session.SessionId, "turn-1", "hello", "hi there", "legacy_chat", "gpt-test", "corr-1", "default", null, null),
            CancellationToken.None);

        var recreated = await TestHelpers.CreateInitializedStoreAsync(path);
        var recent = await recreated.GetRecentTurnsAsync(session.SessionId, 10, CancellationToken.None);

        Assert.Contains(recent, static turn => turn.Role == "user" && turn.Text == "hello");
        Assert.Contains(recent, static turn => turn.Role == "assistant" && turn.Text == "hi there");
    }
}
