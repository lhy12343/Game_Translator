using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace GameTranslator.Gui.Services;

internal sealed class TranslationDatabase
{
    internal const long DefaultMaxCachePayloadBytes = 224L * 1024 * 1024;
    private const long MaxDatabaseBytes = 256L * 1024 * 1024;
    private const int SchemaVersion = 4;
    private readonly string _connectionString;
    private readonly long _maxCachePayloadBytes;

    public TranslationDatabase(
        string databasePath,
        long maxCachePayloadBytes = DefaultMaxCachePayloadBytes)
    {
        if (maxCachePayloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxCachePayloadBytes));
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        _maxCachePayloadBytes = maxCachePayloadBytes;
        Initialize();
    }

    public async Task<string?> ReadCacheAsync(
        string cacheKey,
        string cacheIdentity,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE translation_cache
            SET last_hit_utc = $lastHit
            WHERE cache_key = $key AND cache_identity = $identity
            RETURNING translated_text
            """;
        command.Parameters.AddWithValue("$key", cacheKey);
        command.Parameters.AddWithValue("$identity", cacheIdentity);
        command.Parameters.AddWithValue("$lastHit", DateTimeOffset.UtcNow.ToString("O"));
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task<Dictionary<string, string>> ReadBatchCacheAsync(
        IReadOnlyList<(string CacheKey, string CacheIdentity)> entries,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, string>(entries.Count, StringComparer.Ordinal);
        if (entries.Count == 0) return results;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var lastHit = DateTimeOffset.UtcNow.ToString("O");

        // 一次查询拿回所有命中的译文，再逐条校验 cache_identity 防碰撞
        await using var command = connection.CreateCommand();
        var placeholders = new StringBuilder();
        for (var i = 0; i < entries.Count; i++)
        {
            if (i > 0) placeholders.Append(", ");
            placeholders.Append("$k").Append(i);
            command.Parameters.AddWithValue($"$k{i}", entries[i].CacheKey);
        }
        command.CommandText = $"""
            SELECT cache_key, cache_identity, translated_text
            FROM translation_cache
            WHERE cache_key IN ({placeholders})
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var identityByKey = new Dictionary<string, (string Identity, string Text)>(entries.Count, StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            identityByKey[reader.GetString(0)] = (reader.GetString(1), reader.GetString(2));
        }

        // 批量更新 last_hit_utc
        await reader.DisposeAsync();
        command.Parameters.Clear();
        foreach (var (key, identity) in entries)
        {
            if (identityByKey.TryGetValue(key, out var hit) && hit.Identity == identity)
            {
                results[key] = hit.Text;
                command.Parameters.AddWithValue($"$u{command.Parameters.Count}", key);
            }
        }
        if (results.Count > 0)
        {
            var updatePlaceholders = new StringBuilder();
            for (var i = 0; i < command.Parameters.Count; i++)
            {
                if (i > 0) updatePlaceholders.Append(", ");
                updatePlaceholders.Append("$u").Append(i);
            }
            command.CommandText = $"""
                UPDATE translation_cache SET last_hit_utc = $lastHit
                WHERE cache_key IN ({updatePlaceholders})
                """;
            command.Parameters.AddWithValue("$lastHit", lastHit);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return results;
    }

    public async Task WriteCacheAsync(
        string cacheKey,
        string cacheIdentity,
        string translated,
        CancellationToken cancellationToken)
    {
        var lastHit = DateTimeOffset.UtcNow.ToString("O");
        var payloadBytes = Encoding.UTF8.GetByteCount(cacheKey)
                           + Encoding.UTF8.GetByteCount(cacheIdentity)
                           + Encoding.UTF8.GetByteCount(translated)
                           + Encoding.UTF8.GetByteCount(lastHit);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = "SELECT payload_bytes FROM translation_cache WHERE cache_key = $key";
        command.Parameters.AddWithValue("$key", cacheKey);
        var previousBytes = (long?)await command.ExecuteScalarAsync(cancellationToken) ?? 0L;

        command.CommandText = """
            INSERT INTO translation_cache (cache_key, cache_identity, translated_text, last_hit_utc, payload_bytes)
            VALUES ($key, $identity, $translated, $lastHit, $payloadBytes)
            ON CONFLICT(cache_key) DO UPDATE SET
                cache_identity = excluded.cache_identity,
                translated_text = excluded.translated_text,
                last_hit_utc = excluded.last_hit_utc,
                payload_bytes = excluded.payload_bytes
            """;
        command.Parameters.AddWithValue("$identity", cacheIdentity);
        command.Parameters.AddWithValue("$translated", translated);
        command.Parameters.AddWithValue("$lastHit", lastHit);
        command.Parameters.AddWithValue("$payloadBytes", payloadBytes);
        await command.ExecuteNonQueryAsync(cancellationToken);

        command.CommandText = "SELECT value FROM app_metadata WHERE key = 'cache_payload_bytes'";
        var totalBytes = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L) - previousBytes + payloadBytes;
        while (totalBytes > _maxCachePayloadBytes)
        {
            command.CommandText = "SELECT cache_key, payload_bytes FROM translation_cache ORDER BY last_hit_utc, rowid LIMIT 1";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) break;
            var oldestKey = reader.GetString(0);
            var oldestBytes = reader.GetInt64(1);
            await reader.DisposeAsync();

            command.CommandText = "DELETE FROM translation_cache WHERE cache_key = $oldestKey";
            command.Parameters.AddWithValue("$oldestKey", oldestKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
            command.Parameters.RemoveAt("$oldestKey");
            totalBytes -= oldestBytes;
        }

        command.CommandText = "UPDATE app_metadata SET value = $total WHERE key = 'cache_payload_bytes'";
        command.Parameters.AddWithValue("$total", Math.Max(0, totalBytes));
        await command.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
    }

    public async Task ClearCacheAsync(CancellationToken cancellationToken)
    {
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM translation_cache; UPDATE app_metadata SET value = 0 WHERE key = 'cache_payload_bytes'";
            await command.ExecuteNonQueryAsync(cancellationToken);
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var vacuumConnection = new SqliteConnection(_connectionString);
        await vacuumConnection.OpenAsync(cancellationToken);
        await using var vacuum = vacuumConnection.CreateCommand();
        vacuum.CommandText = "VACUUM";
        await vacuum.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA journal_size_limit = 4194304";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA user_version";
        var version = (long)(command.ExecuteScalar() ?? 0L);
        if (version > SchemaVersion)
            throw new InvalidDataException("翻译数据库来自更高版本，请升级应用后再使用。");

        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS translation_cache (
                cache_key TEXT PRIMARY KEY,
                cache_identity TEXT NOT NULL,
                translated_text TEXT NOT NULL,
                last_hit_utc TEXT NOT NULL,
                payload_bytes INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS app_metadata (
                key TEXT PRIMARY KEY,
                value INTEGER NOT NULL
            );
            INSERT OR IGNORE INTO app_metadata (key, value) VALUES ('cache_payload_bytes', 0);
            """;
        command.ExecuteNonQuery();

        if (version < 3)
        {
            AddColumnIfMissing(connection, transaction, "cache_identity", "TEXT NOT NULL DEFAULT ''");
            AddColumnIfMissing(connection, transaction, "last_hit_utc", "TEXT NOT NULL DEFAULT ''");
            command.CommandText = """
                UPDATE translation_cache SET last_hit_utc = $migrationTime WHERE last_hit_utc = '';
                CREATE TABLE translation_cache_v3 (
                    cache_key TEXT PRIMARY KEY,
                    cache_identity TEXT NOT NULL,
                    translated_text TEXT NOT NULL,
                    last_hit_utc TEXT NOT NULL,
                    payload_bytes INTEGER NOT NULL
                );
                INSERT INTO translation_cache_v3
                    (cache_key, cache_identity, translated_text, last_hit_utc, payload_bytes)
                SELECT cache_key, cache_identity, translated_text, last_hit_utc,
                    length(CAST(cache_key AS BLOB)) +
                    length(CAST(cache_identity AS BLOB)) +
                    length(CAST(translated_text AS BLOB)) +
                    length(CAST(last_hit_utc AS BLOB))
                FROM translation_cache
                WHERE cache_identity <> '';
                DROP TABLE translation_cache;
                ALTER TABLE translation_cache_v3 RENAME TO translation_cache;
                UPDATE app_metadata SET value = (SELECT COALESCE(SUM(payload_bytes), 0) FROM translation_cache)
                WHERE key = 'cache_payload_bytes';
                PRAGMA user_version = 3;
            """;
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$migrationTime", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        if (version < 4)
        {
            command.Parameters.Clear();
            command.CommandText = """
                DROP TABLE IF EXISTS glossary;
                DELETE FROM app_metadata WHERE key = 'glossary_revision';
                PRAGMA user_version = 4;
                """;
            command.ExecuteNonQuery();
        }
        command.Parameters.Clear();
        command.CommandText = "CREATE INDEX IF NOT EXISTS ix_translation_cache_last_hit ON translation_cache(last_hit_utc)";
        command.ExecuteNonQuery();
        transaction.Commit();
        command.Transaction = null;

        if (version < SchemaVersion)
        {
            command.CommandText = "VACUUM";
            command.ExecuteNonQuery();
        }
        command.CommandText = "PRAGMA page_size";
        var pageSize = (long)(command.ExecuteScalar() ?? 4096L);
        command.CommandText = $"PRAGMA max_page_count = {MaxDatabaseBytes / pageSize}";
        command.ExecuteNonQuery();
    }

    private static void AddColumnIfMissing(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string name,
        string definition)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('translation_cache') WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        if ((long)(command.ExecuteScalar() ?? 0L) > 0) return;
        command.CommandText = $"ALTER TABLE translation_cache ADD COLUMN {name} {definition}";
        command.Parameters.Clear();
        command.ExecuteNonQuery();
    }

}
