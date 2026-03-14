namespace ReachTether.Server.Services;

public sealed class SqliteSchemaInitializer(ISqliteConnectionFactory connectionFactory) : ISqliteSchemaInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
                session_id TEXT PRIMARY KEY,
                session_key TEXT NOT NULL,
                user_id TEXT NOT NULL,
                lane TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_active_at TEXT NOT NULL,
                active_personality_id TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_sessions_session_key_user_lane
                ON sessions(session_key, user_id, lane);

            CREATE TABLE IF NOT EXISTS turns (
                turn_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                role TEXT NOT NULL,
                text TEXT NOT NULL,
                created_at TEXT NOT NULL,
                source TEXT NULL,
                model TEXT NULL,
                correlation_id TEXT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_turns_session_created_at
                ON turns(session_id, created_at);

            CREATE TABLE IF NOT EXISTS tool_calls (
                tool_call_id TEXT PRIMARY KEY,
                turn_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                tool_name TEXT NOT NULL,
                arguments_json TEXT NOT NULL,
                output_json TEXT NULL,
                status TEXT NOT NULL,
                created_at TEXT NOT NULL,
                FOREIGN KEY(turn_id) REFERENCES turns(turn_id) ON DELETE CASCADE,
                FOREIGN KEY(session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_tool_calls_session_turn
                ON tool_calls(session_id, turn_id);

            CREATE TABLE IF NOT EXISTS artifacts (
                artifact_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                turn_id TEXT NULL,
                tool_call_id TEXT NULL,
                kind TEXT NOT NULL,
                content_type TEXT NOT NULL,
                content_url_or_path TEXT NOT NULL,
                metadata_json TEXT NULL,
                created_at TEXT NOT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_artifacts_session_turn
                ON artifacts(session_id, turn_id, tool_call_id);

            CREATE TABLE IF NOT EXISTS memory_records (
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
                is_archived INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY(session_id) REFERENCES sessions(session_id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_memory_records_session_archived
                ON memory_records(session_id, is_archived, updated_at);
            CREATE INDEX IF NOT EXISTS idx_memory_records_session_kind
                ON memory_records(session_id, kind);

            CREATE TABLE IF NOT EXISTS memory_vectors (
                memory_id TEXT PRIMARY KEY,
                embedding_provider TEXT NOT NULL,
                embedding_model TEXT NOT NULL,
                embedding_dims INTEGER NOT NULL,
                embedding_json TEXT NOT NULL,
                FOREIGN KEY(memory_id) REFERENCES memory_records(memory_id) ON DELETE CASCADE
            );

            CREATE VIRTUAL TABLE IF NOT EXISTS memory_records_fts USING fts5(
                memory_id UNINDEXED,
                title,
                content,
                summary,
                tokenize = 'unicode61 remove_diacritics 1'
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
