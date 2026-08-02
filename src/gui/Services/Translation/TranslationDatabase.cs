using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace GameTranslator.Gui.Services;

internal sealed class TranslationDatabase
{
    internal const int MaxGlossaryEntries = 100;
    private const int MaxCacheEntries = 10_000;
    private readonly string _connectionString;

    public TranslationDatabase(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        Initialize();
    }

    public IReadOnlyList<GlossaryEntry> LoadGlossary()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, source, target, category FROM glossary ORDER BY id";
        using var reader = command.ExecuteReader();
        var entries = new List<GlossaryEntry>();
        while (reader.Read())
            entries.Add(new GlossaryEntry(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return entries;
    }

    public GlossaryEntry AddGlossary(string source, string target, string category)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM glossary";
        if ((long)(command.ExecuteScalar() ?? 0L) >= MaxGlossaryEntries)
            throw new InvalidOperationException($"术语表最多保存 {MaxGlossaryEntries} 条，请先删除不再使用的术语。");

        command.CommandText = "INSERT INTO glossary (source, target, category) VALUES ($source, $target, $category); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$category", category);
        var id = (long)(command.ExecuteScalar() ?? throw new InvalidOperationException("术语保存失败。"));
        IncrementGlossaryRevision(connection, transaction);
        transaction.Commit();
        return new GlossaryEntry(id, source, target, category);
    }

    public bool DeleteGlossary(long id)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM glossary WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() == 0) return false;
        IncrementGlossaryRevision(connection, transaction);
        transaction.Commit();
        return true;
    }

    public long LoadGlossaryRevision()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_metadata WHERE key = 'glossary_revision'";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public async Task<string?> ReadCacheAsync(string cacheKey, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT translated_text FROM translation_cache WHERE cache_key = $key";
        command.Parameters.AddWithValue("$key", cacheKey);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task WriteCacheAsync(
        string cacheKey,
        string original,
        string translated,
        TranslatorConfig config,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO translation_cache
                (cache_key, original_text, translated_text, source_language, target_language, model, created_utc)
            VALUES
                ($key, $original, $translated, $source, $target, $model, $created)
            ON CONFLICT(cache_key) DO UPDATE SET
                translated_text = excluded.translated_text,
                created_utc = excluded.created_utc;
            DELETE FROM translation_cache
            WHERE rowid IN (
                SELECT rowid FROM translation_cache
                ORDER BY created_utc DESC
                LIMIT -1 OFFSET $maxEntries
            );
            """;
        command.Parameters.AddWithValue("$key", cacheKey);
        command.Parameters.AddWithValue("$original", original);
        command.Parameters.AddWithValue("$translated", translated);
        command.Parameters.AddWithValue("$source", config.SourceLanguage);
        command.Parameters.AddWithValue("$target", config.TargetLanguage);
        command.Parameters.AddWithValue("$model", config.Model);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$maxEntries", MaxCacheEntries);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS translation_cache (
                cache_key TEXT PRIMARY KEY,
                original_text TEXT NOT NULL,
                translated_text TEXT NOT NULL,
                source_language TEXT NOT NULL,
                target_language TEXT NOT NULL,
                model TEXT NOT NULL,
                created_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS glossary (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source TEXT NOT NULL,
                target TEXT NOT NULL,
                category TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS app_metadata (
                key TEXT PRIMARY KEY,
                value INTEGER NOT NULL
            );
            INSERT OR IGNORE INTO app_metadata (key, value) VALUES ('glossary_revision', 0);
            """;
        command.ExecuteNonQuery();
    }

    private static void IncrementGlossaryRevision(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE app_metadata SET value = value + 1 WHERE key = 'glossary_revision'";
        command.ExecuteNonQuery();
    }
}
