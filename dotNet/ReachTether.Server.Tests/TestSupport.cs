using Microsoft.Data.Sqlite;
using System.Net;
using System.Net.Http;
using System.Text;
using ReachTether.Server.Services;

namespace ReachTether.Server.Tests;

internal sealed class TestSqliteConnectionFactory(string databasePath) : ISqliteConnectionFactory
{
    public string DatabasePath { get; } = databasePath;

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var connection = new SqliteConnection($"Data Source={DatabasePath}");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}

internal sealed class FakeMemoryEmbeddingProvider(Func<string, EmbeddingVectorResult> factory) : IMemoryEmbeddingProvider
{
    public Task<EmbeddingVectorResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken)
        => Task.FromResult(factory(request.Input));
}

internal sealed class FakeUserFactExtractionService(
    Func<PersistSessionTurnRequest, UserFactExtractionResult>? extract = null,
    Func<IReadOnlyList<PromptRecentTurn>, string>? summarize = null) : IUserFactExtractionService
{
    public Task<UserFactExtractionResult> ExtractAsync(PersistSessionTurnRequest request, CancellationToken cancellationToken)
        => Task.FromResult(extract?.Invoke(request) ?? new UserFactExtractionResult([], null));

    public Task<string> SummarizeSessionAsync(IReadOnlyList<PromptRecentTurn> recentTurns, CancellationToken cancellationToken)
        => Task.FromResult(summarize?.Invoke(recentTurns) ?? "summary");
}

internal sealed class FakeNamedEmbeddingProvider(string name, bool isAvailable, EmbeddingVectorResult result) : INamedMemoryEmbeddingProvider
{
    public string Name { get; } = name;
    public bool IsAvailable { get; } = isAvailable;

    public Task<EmbeddingVectorResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken)
        => Task.FromResult(result);
}

internal sealed class RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return responder(request);
    }
}

internal static class TestHelpers
{
    public static string CreateTempDbPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "reachtether-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return Path.Combine(root, "test.db");
    }

    public static async Task<SqliteSessionStore> CreateInitializedStoreAsync(string? path = null)
    {
        var databasePath = path ?? CreateTempDbPath();
        var factory = new TestSqliteConnectionFactory(databasePath);
        var initializer = new SqliteSchemaInitializer(factory);
        await initializer.InitializeAsync(CancellationToken.None);
        return new SqliteSessionStore(factory);
    }
}
