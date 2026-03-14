using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace ReachTether.Server.Services;

public sealed class SqliteSessionStore(
    ISqliteConnectionFactory connectionFactory) : ISessionStore
{
    public async Task<StartOrResumeSessionResponse> StartOrResumeSessionAsync(
        StartOrResumeSessionRequest request,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var existing = await FindSessionAsync(connection, request, cancellationToken);
        string sessionId;
        var resumed = existing is not null;
        if (existing is null)
        {
            sessionId = Guid.NewGuid().ToString("n");
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO sessions(session_id, session_key, user_id, lane, created_at, updated_at, last_active_at, active_personality_id)
                VALUES ($sessionId, $sessionKey, $userId, $lane, $createdAt, $updatedAt, $lastActiveAt, $activePersonalityId);
                """;
            insert.Parameters.AddWithValue("$sessionId", sessionId);
            insert.Parameters.AddWithValue("$sessionKey", request.SessionKey);
            insert.Parameters.AddWithValue("$userId", request.UserId);
            insert.Parameters.AddWithValue("$lane", request.Lane);
            insert.Parameters.AddWithValue("$createdAt", ToDb(now));
            insert.Parameters.AddWithValue("$updatedAt", ToDb(now));
            insert.Parameters.AddWithValue("$lastActiveAt", ToDb(now));
            insert.Parameters.AddWithValue("$activePersonalityId", (object?)request.ActivePersonalityId ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            sessionId = existing.Value.SessionId;
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE sessions
                SET updated_at = $updatedAt,
                    last_active_at = $lastActiveAt,
                    active_personality_id = $activePersonalityId
                WHERE session_id = $sessionId;
                """;
            update.Parameters.AddWithValue("$sessionId", sessionId);
            update.Parameters.AddWithValue("$updatedAt", ToDb(now));
            update.Parameters.AddWithValue("$lastActiveAt", ToDb(now));
            update.Parameters.AddWithValue("$activePersonalityId", (object?)request.ActivePersonalityId ?? DBNull.Value);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        var summary = await GetSessionSummaryAsync(sessionId, cancellationToken);
        var recentTurns = await GetRecentTurnsAsync(sessionId, 6, cancellationToken);
        return new StartOrResumeSessionResponse(
            sessionId,
            resumed,
            existing?.ActivePersonalityId ?? request.ActivePersonalityId ?? string.Empty,
            summary,
            recentTurns,
            []);
    }

    public async Task<PersistSessionTurnResponse> PersistSessionTurnAsync(
        PersistSessionTurnRequest request,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var storedTurnIds = new List<string>(2);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await UpdateSessionActivityAsync(connection, transaction, request.SessionId, request.ActivePersonalityId, now, cancellationToken);

        if (!string.IsNullOrWhiteSpace(request.UserText))
        {
            await InsertTurnAsync(connection, transaction, request.TurnId, request.SessionId, "user", request.UserText!, request.Source, request.Model, request.CorrelationId, now, cancellationToken);
            storedTurnIds.Add(request.TurnId);
        }

        if (!string.IsNullOrWhiteSpace(request.AssistantText))
        {
            var assistantTurnId = $"{request.TurnId}:assistant";
            await InsertTurnAsync(connection, transaction, assistantTurnId, request.SessionId, "assistant", request.AssistantText!, request.Source, request.Model, request.CorrelationId, now, cancellationToken);
            storedTurnIds.Add(assistantTurnId);
        }

        if (request.ToolCalls is not null)
        {
            foreach (var toolCall in request.ToolCalls)
            {
                await using var toolCommand = connection.CreateCommand();
                toolCommand.Transaction = transaction;
                toolCommand.CommandText = """
                    INSERT OR REPLACE INTO tool_calls(tool_call_id, turn_id, session_id, tool_name, arguments_json, output_json, status, created_at)
                    VALUES ($toolCallId, $turnId, $sessionId, $toolName, $argumentsJson, $outputJson, $status, $createdAt);
                    """;
                toolCommand.Parameters.AddWithValue("$toolCallId", toolCall.ToolCallId);
                toolCommand.Parameters.AddWithValue("$turnId", request.TurnId);
                toolCommand.Parameters.AddWithValue("$sessionId", request.SessionId);
                toolCommand.Parameters.AddWithValue("$toolName", toolCall.ToolName);
                toolCommand.Parameters.AddWithValue("$argumentsJson", toolCall.ArgumentsJson);
                toolCommand.Parameters.AddWithValue("$outputJson", (object?)toolCall.OutputJson ?? DBNull.Value);
                toolCommand.Parameters.AddWithValue("$status", toolCall.Status);
                toolCommand.Parameters.AddWithValue("$createdAt", ToDb(toolCall.CreatedAt));
                await toolCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        if (request.Artifacts is not null)
        {
            foreach (var artifact in request.Artifacts)
            {
                await InsertArtifactAsync(connection, transaction, request.SessionId, artifact, cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new PersistSessionTurnResponse(true, storedTurnIds, await GetSessionSummaryAsync(request.SessionId, cancellationToken));
    }

    public async Task RecordArtifactMetadataAsync(
        PersistedArtifactDescriptor artifact,
        string sessionId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await InsertArtifactAsync(connection, transaction, sessionId, artifact, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<SessionSummaryDescriptor?> GetSessionSummaryAsync(string sessionId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT memory_id, title, COALESCE(summary, content), updated_at
            FROM memory_records
            WHERE session_id = $sessionId
              AND kind = 'session_summary'
              AND is_archived = 0
            ORDER BY updated_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SessionSummaryDescriptor(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            ParseDbDate(reader.GetString(3)));
    }

    public async Task<IReadOnlyList<PromptRecentTurn>> GetRecentTurnsAsync(string sessionId, int count, CancellationToken cancellationToken)
    {
        var items = new List<PromptRecentTurn>();
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT role, text, created_at
            FROM turns
            WHERE session_id = $sessionId
            ORDER BY created_at DESC
            LIMIT $count;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$count", count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new PromptRecentTurn(
                reader.GetString(0),
                reader.GetString(1),
                ParseDbDate(reader.GetString(2))));
        }

        items.Reverse();
        return items;
    }

    public async Task<IReadOnlyList<StoredMemoryRecord>> SearchMemoryByTextAsync(string sessionId, string query, int topK, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var ftsQuery = BuildSafeFtsQuery(query);
        if (string.IsNullOrWhiteSpace(ftsQuery))
        {
            return await SearchMemoryByLikeAsync(sessionId, query, topK, cancellationToken);
        }

        var items = new List<StoredMemoryRecord>();
        try
        {
            await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT mr.memory_id, mr.session_id, mr.scope, mr.kind, mr.title, mr.content, mr.summary,
                       mr.source_turn_id, mr.importance, mr.created_at, mr.updated_at, mr.last_accessed_at,
                       mr.is_archived, mv.embedding_provider, mv.embedding_model, mv.embedding_dims, mv.embedding_json,
                       bm25(memory_records_fts) AS rank
                FROM memory_records_fts
                JOIN memory_records mr ON mr.memory_id = memory_records_fts.memory_id
                LEFT JOIN memory_vectors mv ON mv.memory_id = mr.memory_id
                WHERE memory_records_fts MATCH $query
                  AND mr.session_id = $sessionId
                  AND mr.is_archived = 0
                ORDER BY rank
                LIMIT $topK;
                """;
            command.Parameters.AddWithValue("$query", ftsQuery);
            command.Parameters.AddWithValue("$sessionId", sessionId);
            command.Parameters.AddWithValue("$topK", topK);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadMemoryRecord(reader, textScore: NormalizeFtsRank(reader.GetDouble(17))));
            }
        }
        catch (SqliteException) when (!string.IsNullOrWhiteSpace(query))
        {
            return await SearchMemoryByLikeAsync(sessionId, query, topK, cancellationToken);
        }

        return items;
    }

    public async Task<IReadOnlyList<StoredMemoryRecord>> GetActiveMemoryRecordsAsync(string sessionId, CancellationToken cancellationToken)
    {
        var items = new List<StoredMemoryRecord>();
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT mr.memory_id, mr.session_id, mr.scope, mr.kind, mr.title, mr.content, mr.summary,
                   mr.source_turn_id, mr.importance, mr.created_at, mr.updated_at, mr.last_accessed_at,
                   mr.is_archived, mv.embedding_provider, mv.embedding_model, mv.embedding_dims, mv.embedding_json
            FROM memory_records mr
            LEFT JOIN memory_vectors mv ON mv.memory_id = mr.memory_id
            WHERE mr.session_id = $sessionId
              AND mr.is_archived = 0;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadMemoryRecord(reader));
        }

        return items;
    }

    public async Task<PromoteMemoryResponse> UpsertMemoryAsync(
        PromoteMemoryRequest request,
        string? existingMemoryId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var created = string.IsNullOrWhiteSpace(existingMemoryId);
        var memoryId = existingMemoryId ?? Guid.NewGuid().ToString("n");

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO memory_records(memory_id, session_id, scope, kind, title, content, summary, source_turn_id, importance, created_at, updated_at, last_accessed_at, is_archived)
                VALUES ($memoryId, $sessionId, $scope, $kind, $title, $content, $summary, $sourceTurnId, $importance, $createdAt, $updatedAt, NULL, 0)
                ON CONFLICT(memory_id) DO UPDATE SET
                    scope = excluded.scope,
                    kind = excluded.kind,
                    title = excluded.title,
                    content = excluded.content,
                    summary = excluded.summary,
                    source_turn_id = excluded.source_turn_id,
                    importance = excluded.importance,
                    updated_at = excluded.updated_at,
                    is_archived = 0;
                """;
            command.Parameters.AddWithValue("$memoryId", memoryId);
            command.Parameters.AddWithValue("$sessionId", request.SessionId);
            command.Parameters.AddWithValue("$scope", request.Scope);
            command.Parameters.AddWithValue("$kind", request.Kind);
            command.Parameters.AddWithValue("$title", request.Title);
            command.Parameters.AddWithValue("$content", request.Content);
            command.Parameters.AddWithValue("$summary", (object?)request.Summary ?? DBNull.Value);
            command.Parameters.AddWithValue("$sourceTurnId", (object?)request.SourceTurnId ?? DBNull.Value);
            command.Parameters.AddWithValue("$importance", request.Importance);
            command.Parameters.AddWithValue("$createdAt", ToDb(now));
            command.Parameters.AddWithValue("$updatedAt", ToDb(now));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await UpsertFtsAsync(connection, transaction, memoryId, request.Title, request.Content, request.Summary, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PromoteMemoryResponse(memoryId, created, now);
    }

    public async Task UpsertMemoryVectorAsync(string memoryId, EmbeddingVectorResult embedding, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory_vectors(memory_id, embedding_provider, embedding_model, embedding_dims, embedding_json)
            VALUES ($memoryId, $provider, $model, $dims, $embedding)
            ON CONFLICT(memory_id) DO UPDATE SET
                embedding_provider = excluded.embedding_provider,
                embedding_model = excluded.embedding_model,
                embedding_dims = excluded.embedding_dims,
                embedding_json = excluded.embedding_json;
            """;
        command.Parameters.AddWithValue("$memoryId", memoryId);
        command.Parameters.AddWithValue("$provider", embedding.Provider);
        command.Parameters.AddWithValue("$model", embedding.Model);
        command.Parameters.AddWithValue("$dims", embedding.Dimensions);
        command.Parameters.AddWithValue("$embedding", JsonSerializer.Serialize(embedding.Embedding));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StoredMemoryRecord>> SearchMemoryForAdminAsync(
        string? sessionId,
        string? query,
        bool includeArchived,
        int topK,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            var all = new List<StoredMemoryRecord>();
            await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT mr.memory_id, mr.session_id, mr.scope, mr.kind, mr.title, mr.content, mr.summary,
                       mr.source_turn_id, mr.importance, mr.created_at, mr.updated_at, mr.last_accessed_at,
                       mr.is_archived, mv.embedding_provider, mv.embedding_model, mv.embedding_dims, mv.embedding_json,
                       bm25(memory_records_fts) AS rank
                FROM memory_records_fts
                JOIN memory_records mr ON mr.memory_id = memory_records_fts.memory_id
                LEFT JOIN memory_vectors mv ON mv.memory_id = mr.memory_id
                WHERE memory_records_fts MATCH $query
                  AND ($sessionId IS NULL OR mr.session_id = $sessionId)
                  AND ($includeArchived = 1 OR mr.is_archived = 0)
                ORDER BY rank
                LIMIT $topK;
                """;
            command.Parameters.AddWithValue("$query", query);
            command.Parameters.AddWithValue("$sessionId", (object?)sessionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$includeArchived", includeArchived ? 1 : 0);
            command.Parameters.AddWithValue("$topK", topK);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                all.Add(ReadMemoryRecord(reader, NormalizeFtsRank(reader.GetDouble(17))));
            }

            return all;
        }

        await using var allConnection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var allCommand = allConnection.CreateCommand();
        allCommand.CommandText = """
            SELECT mr.memory_id, mr.session_id, mr.scope, mr.kind, mr.title, mr.content, mr.summary,
                   mr.source_turn_id, mr.importance, mr.created_at, mr.updated_at, mr.last_accessed_at,
                   mr.is_archived, mv.embedding_provider, mv.embedding_model, mv.embedding_dims, mv.embedding_json
            FROM memory_records mr
            LEFT JOIN memory_vectors mv ON mv.memory_id = mr.memory_id
            WHERE ($sessionId IS NULL OR mr.session_id = $sessionId)
              AND ($includeArchived = 1 OR mr.is_archived = 0)
            ORDER BY mr.updated_at DESC
            LIMIT $topK;
            """;
        allCommand.Parameters.AddWithValue("$sessionId", (object?)sessionId ?? DBNull.Value);
        allCommand.Parameters.AddWithValue("$includeArchived", includeArchived ? 1 : 0);
        allCommand.Parameters.AddWithValue("$topK", topK);
        var fallback = new List<StoredMemoryRecord>();
        await using var allReader = await allCommand.ExecuteReaderAsync(cancellationToken);
        while (await allReader.ReadAsync(cancellationToken))
        {
            fallback.Add(ReadMemoryRecord(allReader));
        }

        return fallback;
    }

    public async Task<ArchiveMemoryResponse> ArchiveMemoryAsync(string memoryId, CancellationToken cancellationToken)
        => await SetArchiveStateAsync(memoryId, archived: true, cancellationToken);

    public async Task<RestoreMemoryResponse> RestoreMemoryAsync(string memoryId, CancellationToken cancellationToken)
    {
        var result = await SetArchiveStateAsync(memoryId, archived: false, cancellationToken);
        return new RestoreMemoryResponse(result.MemoryId, result.Archived, result.UpdatedAt);
    }

    public async Task<IReadOnlyList<StoredMemoryRecord>> GetMemoryRecordsForReindexAsync(
        string? sessionId,
        IReadOnlyList<string>? memoryIds,
        CancellationToken cancellationToken)
    {
        var queryByIds = memoryIds is { Count: > 0 };
        var sql = queryByIds
            ? $"""
               SELECT mr.memory_id, mr.session_id, mr.scope, mr.kind, mr.title, mr.content, mr.summary,
                      mr.source_turn_id, mr.importance, mr.created_at, mr.updated_at, mr.last_accessed_at,
                      mr.is_archived, mv.embedding_provider, mv.embedding_model, mv.embedding_dims, mv.embedding_json
               FROM memory_records mr
               LEFT JOIN memory_vectors mv ON mv.memory_id = mr.memory_id
               WHERE mr.memory_id IN ({string.Join(", ", memoryIds!.Select((_, i) => $"$id{i}"))})
                 AND ($sessionId IS NULL OR mr.session_id = $sessionId);
               """
            : """
               SELECT mr.memory_id, mr.session_id, mr.scope, mr.kind, mr.title, mr.content, mr.summary,
                      mr.source_turn_id, mr.importance, mr.created_at, mr.updated_at, mr.last_accessed_at,
                      mr.is_archived, mv.embedding_provider, mv.embedding_model, mv.embedding_dims, mv.embedding_json
               FROM memory_records mr
               LEFT JOIN memory_vectors mv ON mv.memory_id = mr.memory_id
               WHERE ($sessionId IS NULL OR mr.session_id = $sessionId);
               """;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$sessionId", (object?)sessionId ?? DBNull.Value);
        if (queryByIds)
        {
            for (var i = 0; i < memoryIds!.Count; i++)
            {
                command.Parameters.AddWithValue($"$id{i}", memoryIds[i]);
            }
        }

        var items = new List<StoredMemoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadMemoryRecord(reader));
        }

        return items;
    }

    private async Task<(string SessionId, string ActivePersonalityId)?> FindSessionAsync(
        SqliteConnection connection,
        StartOrResumeSessionRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, COALESCE(active_personality_id, '')
            FROM sessions
            WHERE session_key = $sessionKey
              AND user_id = $userId
              AND lane = $lane
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionKey", request.SessionKey);
        command.Parameters.AddWithValue("$userId", request.UserId);
        command.Parameters.AddWithValue("$lane", request.Lane);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task InsertTurnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string turnId,
        string sessionId,
        string role,
        string text,
        string source,
        string? model,
        string? correlationId,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO turns(turn_id, session_id, role, text, created_at, source, model, correlation_id)
            VALUES ($turnId, $sessionId, $role, $text, $createdAt, $source, $model, $correlationId);
            """;
        command.Parameters.AddWithValue("$turnId", turnId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$createdAt", ToDb(createdAt));
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
        command.Parameters.AddWithValue("$correlationId", (object?)correlationId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateSessionActivityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        string? activePersonalityId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE sessions
            SET updated_at = $updatedAt,
                last_active_at = $lastActiveAt,
                active_personality_id = COALESCE($activePersonalityId, active_personality_id)
            WHERE session_id = $sessionId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$updatedAt", ToDb(now));
        command.Parameters.AddWithValue("$lastActiveAt", ToDb(now));
        command.Parameters.AddWithValue("$activePersonalityId", (object?)activePersonalityId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertArtifactAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        PersistedArtifactDescriptor artifact,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO artifacts(artifact_id, session_id, turn_id, tool_call_id, kind, content_type, content_url_or_path, metadata_json, created_at)
            VALUES ($artifactId, $sessionId, $turnId, $toolCallId, $kind, $contentType, $contentUrlOrPath, $metadataJson, $createdAt);
            """;
        command.Parameters.AddWithValue("$artifactId", artifact.ArtifactId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$turnId", (object?)artifact.TurnId ?? DBNull.Value);
        command.Parameters.AddWithValue("$toolCallId", (object?)artifact.ToolCallId ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", artifact.Kind);
        command.Parameters.AddWithValue("$contentType", artifact.ContentType);
        command.Parameters.AddWithValue("$contentUrlOrPath", artifact.ContentUrlOrPath);
        command.Parameters.AddWithValue("$metadataJson", (object?)artifact.MetadataJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", ToDb(artifact.CreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertFtsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string memoryId,
        string title,
        string content,
        string? summary,
        CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM memory_records_fts WHERE memory_id = $memoryId;";
            delete.Parameters.AddWithValue("$memoryId", memoryId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO memory_records_fts(memory_id, title, content, summary)
            VALUES ($memoryId, $title, $content, $summary);
            """;
        insert.Parameters.AddWithValue("$memoryId", memoryId);
        insert.Parameters.AddWithValue("$title", title);
        insert.Parameters.AddWithValue("$content", content);
        insert.Parameters.AddWithValue("$summary", (object?)summary ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<ArchiveMemoryResponse> SetArchiveStateAsync(string memoryId, bool archived, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE memory_records
            SET is_archived = $archived,
                updated_at = $updatedAt
            WHERE memory_id = $memoryId;
            """;
        command.Parameters.AddWithValue("$memoryId", memoryId);
        command.Parameters.AddWithValue("$archived", archived ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", ToDb(now));
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
        {
            throw new InvalidOperationException($"Memory record '{memoryId}' was not found.");
        }

        return new ArchiveMemoryResponse(memoryId, archived, now);
    }

    private static StoredMemoryRecord ReadMemoryRecord(SqliteDataReader reader, double textScore = 0)
    {
        return new StoredMemoryRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetDouble(8),
            ParseDbDate(reader.GetString(9)),
            ParseDbDate(reader.GetString(10)),
            reader.IsDBNull(11) ? null : ParseDbDate(reader.GetString(11)),
            reader.GetInt32(12) != 0,
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetInt32(15),
            reader.IsDBNull(16) ? null : JsonSerializer.Deserialize<List<float>>(reader.GetString(16)),
            textScore);
    }

    private static string ToDb(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDbDate(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static double NormalizeFtsRank(double rawRank)
        => 1d / (1d + Math.Max(0d, rawRank * -1d));

    private async Task<IReadOnlyList<StoredMemoryRecord>> SearchMemoryByLikeAsync(
        string sessionId,
        string query,
        int topK,
        CancellationToken cancellationToken)
    {
        var items = new List<StoredMemoryRecord>();
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT mr.memory_id, mr.session_id, mr.scope, mr.kind, mr.title, mr.content, mr.summary,
                   mr.source_turn_id, mr.importance, mr.created_at, mr.updated_at, mr.last_accessed_at,
                   mr.is_archived, mv.embedding_provider, mv.embedding_model, mv.embedding_dims, mv.embedding_json
            FROM memory_records mr
            LEFT JOIN memory_vectors mv ON mv.memory_id = mr.memory_id
            WHERE mr.session_id = $sessionId
              AND mr.is_archived = 0
              AND (
                    mr.title LIKE $pattern
                 OR mr.content LIKE $pattern
                 OR COALESCE(mr.summary, '') LIKE $pattern
              )
            ORDER BY mr.updated_at DESC
            LIMIT $topK;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$pattern", $"%{query}%");
        command.Parameters.AddWithValue("$topK", topK);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadMemoryRecord(reader, textScore: 0.2));
        }

        return items;
    }

    private static string BuildSafeFtsQuery(string query)
    {
        var tokens = Regex.Matches(query, @"[\p{L}\p{N}_-]+")
            .Select(static match => match.Value.Trim())
            .Where(static token => !string.IsNullOrWhiteSpace(token))
            .Take(6)
            .Select(static token => $"\"{token.Replace("\"", "\"\"")}\"")
            .ToArray();

        return string.Join(" ", tokens);
    }
}
