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
                INSERT INTO sessions(session_id, session_key, user_id, lane, created_at, updated_at, last_active_at, active_personality_id, active_profile_id)
                VALUES ($sessionId, $sessionKey, $userId, $lane, $createdAt, $updatedAt, $lastActiveAt, $activePersonalityId, NULL);
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
        var activeProfile = await GetActiveProfileAsync(sessionId, cancellationToken);
        return new StartOrResumeSessionResponse(
            sessionId,
            resumed,
            existing?.ActivePersonalityId ?? request.ActivePersonalityId ?? string.Empty,
            activeProfile,
            summary,
            recentTurns,
            await GetPendingSystemEventsAsync(sessionId, cancellationToken));
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

    public async Task LinkSessionToProfileAsync(
        string sessionId,
        string? profileId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sessions
            SET active_profile_id = $profileId,
                updated_at = $updatedAt
            WHERE session_id = $sessionId;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$profileId", (object?)profileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", ToDb(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    public async Task<ActiveProfileDescriptor?> GetActiveProfileAsync(string sessionId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.profile_id, p.display_name, COALESCE(p.summary, ''), p.updated_at
            FROM sessions s
            JOIN profiles p ON p.profile_id = s.active_profile_id
            WHERE s.session_id = $sessionId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ActiveProfileDescriptor(
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

    public async Task<IReadOnlyList<PendingSystemEventDescriptor>> GetPendingSystemEventsAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var items = new List<PendingSystemEventDescriptor>();
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, title, summary
            FROM pending_system_events
            WHERE session_id = $sessionId
              AND status = 'pending'
            ORDER BY updated_at DESC
            LIMIT 4;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new PendingSystemEventDescriptor(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

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
        var activeProfileId = await GetActiveProfileIdAsync(sessionId, cancellationToken);
        try
        {
            await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT mr.memory_id, mr.session_id, mr.profile_id, mr.scope, mr.kind, mr.attribute_name, mr.title, mr.content, mr.summary,
                       mr.normalized_value, mr.source_turn_id, mr.importance, mr.created_at, mr.updated_at, mr.last_accessed_at,
                       mr.is_archived, mv.embedding_provider, mv.embedding_model, mv.embedding_dims, mv.embedding_json,
                       bm25(memory_records_fts) AS rank
                FROM memory_records_fts
                JOIN memory_records mr ON mr.memory_id = memory_records_fts.memory_id
                LEFT JOIN memory_vectors mv ON mv.memory_id = mr.memory_id
                WHERE memory_records_fts MATCH $query
                  AND (mr.session_id = $sessionId OR ($activeProfileId IS NOT NULL AND mr.profile_id = $activeProfileId))
                  AND mr.is_archived = 0
                ORDER BY rank
                LIMIT $topK;
                """;
            command.Parameters.AddWithValue("$query", ftsQuery);
            command.Parameters.AddWithValue("$sessionId", sessionId);
            command.Parameters.AddWithValue("$activeProfileId", (object?)activeProfileId ?? DBNull.Value);
            command.Parameters.AddWithValue("$topK", topK);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadMemoryRecord(reader, textScore: NormalizeFtsRank(reader.GetDouble(20))));
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
        var activeProfileId = await GetActiveProfileIdAsync(sessionId, cancellationToken);
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT mr.memory_id, mr.session_id, mr.profile_id, mr.scope, mr.kind, mr.attribute_name, mr.title, mr.content, mr.summary,
                   mr.normalized_value, mr.source_turn_id, mr.importance, mr.created_at, mr.updated_at, mr.last_accessed_at,
                   mr.is_archived, mv.embedding_provider, mv.embedding_model, mv.embedding_dims, mv.embedding_json
            FROM memory_records mr
            LEFT JOIN memory_vectors mv ON mv.memory_id = mr.memory_id
            WHERE (mr.session_id = $sessionId OR ($activeProfileId IS NOT NULL AND mr.profile_id = $activeProfileId))
              AND mr.is_archived = 0;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$activeProfileId", (object?)activeProfileId ?? DBNull.Value);
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
                INSERT INTO memory_records(memory_id, session_id, profile_id, scope, kind, attribute_name, title, content, summary, normalized_value, source_turn_id, importance, created_at, updated_at, last_accessed_at, is_archived)
                VALUES ($memoryId, $sessionId, $profileId, $scope, $kind, $attributeName, $title, $content, $summary, $normalizedValue, $sourceTurnId, $importance, $createdAt, $updatedAt, NULL, 0)
                ON CONFLICT(memory_id) DO UPDATE SET
                    profile_id = excluded.profile_id,
                    scope = excluded.scope,
                    kind = excluded.kind,
                    attribute_name = excluded.attribute_name,
                    title = excluded.title,
                    content = excluded.content,
                    summary = excluded.summary,
                    normalized_value = excluded.normalized_value,
                    source_turn_id = excluded.source_turn_id,
                    importance = excluded.importance,
                    updated_at = excluded.updated_at,
                    is_archived = 0;
                """;
            command.Parameters.AddWithValue("$memoryId", memoryId);
            command.Parameters.AddWithValue("$sessionId", request.SessionId);
            command.Parameters.AddWithValue("$profileId", (object?)request.ProfileId ?? DBNull.Value);
            command.Parameters.AddWithValue("$scope", request.Scope);
            command.Parameters.AddWithValue("$kind", request.Kind);
            command.Parameters.AddWithValue("$attributeName", (object?)request.AttributeName ?? DBNull.Value);
            command.Parameters.AddWithValue("$title", request.Title);
            command.Parameters.AddWithValue("$content", request.Content);
            command.Parameters.AddWithValue("$summary", (object?)request.Summary ?? DBNull.Value);
            command.Parameters.AddWithValue("$normalizedValue", (object?)request.NormalizedValue ?? DBNull.Value);
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
                SELECT mr.memory_id, mr.session_id, mr.profile_id, mr.scope, mr.kind, mr.attribute_name, mr.title, mr.content, mr.summary,
                       mr.normalized_value, mr.source_turn_id, mr.importance, mr.created_at, mr.updated_at, mr.last_accessed_at,
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
                all.Add(ReadMemoryRecord(reader, NormalizeFtsRank(reader.GetDouble(20))));
            }

            return all;
        }

        await using var allConnection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var allCommand = allConnection.CreateCommand();
        allCommand.CommandText = """
            SELECT mr.memory_id, mr.session_id, mr.profile_id, mr.scope, mr.kind, mr.attribute_name, mr.title, mr.content, mr.summary,
                   mr.normalized_value, mr.source_turn_id, mr.importance, mr.created_at, mr.updated_at, mr.last_accessed_at,
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
               SELECT mr.memory_id, mr.session_id, mr.profile_id, mr.scope, mr.kind, mr.attribute_name, mr.title, mr.content, mr.summary,
                      mr.normalized_value, mr.source_turn_id, mr.importance, mr.created_at, mr.updated_at, mr.last_accessed_at,
                      mr.is_archived, mv.embedding_provider, mv.embedding_model, mv.embedding_dims, mv.embedding_json
               FROM memory_records mr
               LEFT JOIN memory_vectors mv ON mv.memory_id = mr.memory_id
               WHERE mr.memory_id IN ({string.Join(", ", memoryIds!.Select((_, i) => $"$id{i}"))})
                 AND ($sessionId IS NULL OR mr.session_id = $sessionId);
               """
            : """
               SELECT mr.memory_id, mr.session_id, mr.profile_id, mr.scope, mr.kind, mr.attribute_name, mr.title, mr.content, mr.summary,
                      mr.normalized_value, mr.source_turn_id, mr.importance, mr.created_at, mr.updated_at, mr.last_accessed_at,
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

    public async Task<string?> FindExistingMemoryIdAsync(
        string sessionId,
        string scope,
        string kind,
        string? attributeName,
        string? profileId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT memory_id
            FROM memory_records
            WHERE session_id = $sessionId
              AND scope = $scope
              AND kind = $kind
              AND (($attributeName IS NULL AND attribute_name IS NULL) OR attribute_name = $attributeName)
              AND (($profileId IS NULL AND profile_id IS NULL) OR profile_id = $profileId)
              AND is_archived = 0
            ORDER BY updated_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$attributeName", (object?)attributeName ?? DBNull.Value);
        command.Parameters.AddWithValue("$profileId", (object?)profileId ?? DBNull.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value as string;
    }

    public async Task<IReadOnlyList<StoredMemoryRecord>> GetProfileMemoryRecordsAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        var items = new List<StoredMemoryRecord>();
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT mr.memory_id, mr.session_id, mr.profile_id, mr.scope, mr.kind, mr.attribute_name, mr.title, mr.content, mr.summary,
                   mr.normalized_value, mr.source_turn_id, mr.importance, mr.created_at, mr.updated_at, mr.last_accessed_at,
                   mr.is_archived, mv.embedding_provider, mv.embedding_model, mv.embedding_dims, mv.embedding_json
            FROM memory_records mr
            LEFT JOIN memory_vectors mv ON mv.memory_id = mr.memory_id
            WHERE mr.profile_id = $profileId
              AND mr.is_archived = 0
            ORDER BY mr.updated_at DESC;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadMemoryRecord(reader));
        }

        return items;
    }

    public async Task<IReadOnlyList<StoredProfileRecord>> FindProfilesByNormalizedNameAsync(
        string normalizedName,
        CancellationToken cancellationToken)
    {
        var items = new List<StoredProfileRecord>();
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT profile_id, display_name, normalized_name, COALESCE(summary, ''), created_at, updated_at
            FROM profiles
            WHERE normalized_name = $normalizedName
            ORDER BY updated_at DESC;
            """;
        command.Parameters.AddWithValue("$normalizedName", normalizedName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new StoredProfileRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                ParseDbDate(reader.GetString(4)),
                ParseDbDate(reader.GetString(5))));
        }

        return items;
    }

    public async Task<IReadOnlyList<StoredProfileRecord>> ListProfilesAsync(CancellationToken cancellationToken)
    {
        var items = new List<StoredProfileRecord>();
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT profile_id, display_name, normalized_name, COALESCE(summary, ''), created_at, updated_at
            FROM profiles
            ORDER BY updated_at DESC, display_name ASC;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new StoredProfileRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                ParseDbDate(reader.GetString(4)),
                ParseDbDate(reader.GetString(5))));
        }

        return items;
    }

    public async Task<string?> GetMostRecentlyActiveProfileIdAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT active_profile_id
            FROM sessions
            WHERE active_profile_id IS NOT NULL
            ORDER BY last_active_at DESC
            LIMIT 1;
            """;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value as string;
    }

    public async Task<StoredProfileRecord> CreateProfileAsync(
        string displayName,
        string normalizedName,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var profileId = Guid.NewGuid().ToString("n");
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO profiles(profile_id, display_name, normalized_name, summary, created_at, updated_at)
            VALUES ($profileId, $displayName, $normalizedName, '', $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$displayName", displayName);
        command.Parameters.AddWithValue("$normalizedName", normalizedName);
        command.Parameters.AddWithValue("$createdAt", ToDb(now));
        command.Parameters.AddWithValue("$updatedAt", ToDb(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new StoredProfileRecord(profileId, displayName, normalizedName, string.Empty, now, now);
    }

    public async Task UpdateProfileSummaryAsync(
        string profileId,
        string displayName,
        string summary,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE profiles
            SET display_name = $displayName,
                summary = $summary,
                updated_at = $updatedAt
            WHERE profile_id = $profileId;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);
        command.Parameters.AddWithValue("$displayName", displayName);
        command.Parameters.AddWithValue("$summary", summary);
        command.Parameters.AddWithValue("$updatedAt", ToDb(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertPendingSystemEventAsync(
        string sessionId,
        string eventKind,
        string title,
        string summary,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pending_system_events(event_id, session_id, event_kind, title, summary, status, created_at, updated_at)
            VALUES ($eventId, $sessionId, $eventKind, $title, $summary, 'pending', $createdAt, $updatedAt)
            ON CONFLICT(event_id) DO UPDATE SET
                title = excluded.title,
                summary = excluded.summary,
                status = 'pending',
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$eventId", $"{sessionId}:{eventKind}");
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$eventKind", eventKind);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$summary", summary);
        command.Parameters.AddWithValue("$createdAt", ToDb(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$updatedAt", ToDb(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private async Task<string?> GetActiveProfileIdAsync(string sessionId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT active_profile_id
            FROM sessions
            WHERE session_id = $sessionId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string profileId && !string.IsNullOrWhiteSpace(profileId) ? profileId : null;
    }

    private static StoredMemoryRecord ReadMemoryRecord(SqliteDataReader reader, double textScore = 0)
    {
        return new StoredMemoryRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetDouble(11),
            ParseDbDate(reader.GetString(12)),
            ParseDbDate(reader.GetString(13)),
            reader.IsDBNull(14) ? null : ParseDbDate(reader.GetString(14)),
            reader.GetInt32(15) != 0,
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetInt32(18),
            reader.IsDBNull(19) ? null : JsonSerializer.Deserialize<List<float>>(reader.GetString(19)),
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
        var activeProfileId = await GetActiveProfileIdAsync(sessionId, cancellationToken);
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT mr.memory_id, mr.session_id, mr.profile_id, mr.scope, mr.kind, mr.attribute_name, mr.title, mr.content, mr.summary,
                   mr.normalized_value, mr.source_turn_id, mr.importance, mr.created_at, mr.updated_at, mr.last_accessed_at,
                   mr.is_archived, mv.embedding_provider, mv.embedding_model, mv.embedding_dims, mv.embedding_json
            FROM memory_records mr
            LEFT JOIN memory_vectors mv ON mv.memory_id = mr.memory_id
            WHERE (mr.session_id = $sessionId OR ($activeProfileId IS NOT NULL AND mr.profile_id = $activeProfileId))
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
        command.Parameters.AddWithValue("$activeProfileId", (object?)activeProfileId ?? DBNull.Value);
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
