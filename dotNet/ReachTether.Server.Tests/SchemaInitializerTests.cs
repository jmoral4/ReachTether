using Microsoft.Data.Sqlite;
using ReachTether.Server.Services;

namespace ReachTether.Server.Tests;

public sealed class SchemaInitializerTests
{
    [Fact]
    public async Task InitializeAsync_UpgradesLegacyDatabaseWithProfileColumnsAndIndexes()
    {
        var path = TestHelpers.CreateTempDbPath();
        await CreateLegacyDatabaseAsync(path);

        var factory = new TestSqliteConnectionFactory(path);
        var initializer = new SqliteSchemaInitializer(factory);

        await initializer.InitializeAsync(CancellationToken.None);

        await using var connection = await factory.OpenConnectionAsync(CancellationToken.None);
        Assert.True(await ColumnExistsAsync(connection, "sessions", "active_profile_id"));
        Assert.True(await ColumnExistsAsync(connection, "memory_records", "profile_id"));
        Assert.True(await ColumnExistsAsync(connection, "memory_records", "attribute_name"));
        Assert.True(await ColumnExistsAsync(connection, "memory_records", "normalized_value"));
        Assert.True(await IndexExistsAsync(connection, "idx_sessions_active_profile"));
        Assert.True(await IndexExistsAsync(connection, "idx_memory_records_profile_archived"));
        Assert.True(await IndexExistsAsync(connection, "idx_memory_records_profile_attribute"));
    }

    private static async Task CreateLegacyDatabaseAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE sessions (
                session_id TEXT PRIMARY KEY,
                session_key TEXT NOT NULL,
                user_id TEXT NOT NULL,
                lane TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_active_at TEXT NOT NULL,
                active_personality_id TEXT NULL
            );
            CREATE UNIQUE INDEX idx_sessions_session_key_user_lane
                ON sessions(session_key, user_id, lane);

            CREATE TABLE memory_records (
                memory_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                scope TEXT NOT NULL,
                kind TEXT NOT NULL,
                title TEXT NOT NULL,
                content TEXT NOT NULL,
                summary TEXT NULL,
                source_turn_id TEXT NULL,
                importance REAL NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_accessed_at TEXT NULL,
                is_archived INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX idx_memory_records_session_archived
                ON memory_records(session_id, is_archived, updated_at);
            CREATE INDEX idx_memory_records_session_kind
                ON memory_records(session_id, kind);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string tableName, string columnName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> IndexExistsAsync(SqliteConnection connection, string indexName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", indexName);
        var result = await command.ExecuteScalarAsync();
        return result is not null;
    }
}
