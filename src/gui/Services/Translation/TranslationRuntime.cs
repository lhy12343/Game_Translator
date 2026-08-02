using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace GameTranslator.Gui.Services;

public sealed class TranslationRuntime
{
    private const int MaxTextLength = 20_000;
    private const long MaxMemoryCacheBytes = 32L * 1024 * 1024;
    private const int PromptVersion = 1;
    private readonly TranslationSettingsStore _settings;
    private readonly TranslationDatabase _database;
    private readonly Channel<TranslationWorkItem> _queue;
    private readonly ConcurrentDictionary<string, SharedTranslation> _inflight = new();
    private readonly Dictionary<string, string> _memoryCache = [];
    private readonly object _memoryCacheLock = new();
    private readonly string? _databaseRecoveryWarning;
    private long _memoryCacheBytes;
    private TranslatorConfig _config;
    private long _requestCount;
    private long _completedCount;
    private long _cacheHitCount;
    private long _lastLatencyMs = -1;
    private long _glossaryRevision;
    private int _queueLength;

    public TranslatorConfig CurrentConfig => _config;
    public string? ConfigurationLoadError => _databaseRecoveryWarning ?? _settings.LoadError;
    public XUnityBridgeServer Bridge { get; }

    public TranslationRuntime()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameTranslator");
        Directory.CreateDirectory(dataDirectory);
        _settings = new TranslationSettingsStore(dataDirectory);
        _database = OpenDatabaseWithRecovery(
            Path.Combine(dataDirectory, "translations.db"),
            out _databaseRecoveryWarning);
        _glossaryRevision = _database.LoadGlossaryRevision();
        _config = _settings.Load();
        _queue = Channel.CreateBounded<TranslationWorkItem>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

        _ = RunWorkerAsync();
        _ = RunWorkerAsync();
        Bridge = new XUnityBridgeServer(this, dataDirectory);
    }

    public TranslatorConfig SaveConfig(TranslatorConfig candidate)
    {
        var config = ResolveAndValidateConfig(candidate);
        _settings.Save(config);
        _config = config;
        return config;
    }

    public async Task TestConnectionAsync(TranslatorConfig candidate, CancellationToken cancellationToken)
    {
        var config = ResolveAndValidateConfig(candidate);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
        await OpenAiCompatibleClient.SendAsync(
            config,
            "Reply with exactly: OK",
            "You are a connection test. Reply only with OK.",
            timeout.Token);
    }

    public Task<TranslationResult> TranslateAsync(string text, CancellationToken cancellationToken) =>
        TranslateAsync(text, null, null, cancellationToken);

    public async Task<TranslationResult> TranslateAsync(
        string text,
        string? sourceLanguage,
        string? targetLanguage,
        CancellationToken cancellationToken)
    {
        text = text.Trim();
        if (text.Length == 0) throw new InvalidOperationException("请输入要翻译的文本。");
        if (text.Length > MaxTextLength)
            throw new InvalidOperationException($"单次文本不能超过 {MaxTextLength} 个字符。");

        var config = ApplyRequestedLanguages(ResolveAndValidateConfig(_config), sourceLanguage, targetLanguage);
        var started = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _requestCount);
        var cacheIdentity = BuildCacheIdentity(text, config, Interlocked.Read(ref _glossaryRevision));
        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheIdentity)));

        lock (_memoryCacheLock)
        {
            if (_memoryCache.TryGetValue(cacheIdentity, out var memoryTranslation))
                return Complete(memoryTranslation, TranslationSource.MemoryCache, started, true);
        }

        var persistentTranslation = await _database.ReadCacheAsync(cacheKey, cacheIdentity, cancellationToken);
        if (persistentTranslation is not null)
        {
            AddMemoryCache(cacheIdentity, persistentTranslation);
            return Complete(persistentTranslation, TranslationSource.PersistentCache, started, true);
        }

        var created = new SharedTranslation(
            token => QueueTranslationAsync(cacheKey, cacheIdentity, text, config, token),
            shared => RemoveInflight(cacheIdentity, shared));
        var shared = _inflight.GetOrAdd(cacheIdentity, created);
        if (!ReferenceEquals(shared, created)) created.Dispose();
        shared.AddWaiter();
        try
        {
            var result = await shared.Task.WaitAsync(cancellationToken);
            return Complete(result.Text, result.Source, started, false);
        }
        finally
        {
            if (shared.RemoveWaiter())
            {
                RemoveInflight(cacheIdentity, shared);
                shared.Cancel();
            }
        }
    }

    public TranslationMetrics GetMetrics() => new(
        Interlocked.Read(ref _requestCount),
        Interlocked.Read(ref _completedCount),
        Interlocked.Read(ref _cacheHitCount),
        Interlocked.Read(ref _lastLatencyMs),
        Volatile.Read(ref _queueLength));

    public IReadOnlyList<GlossaryEntry> LoadGlossary() => _database.LoadGlossary();

    public GlossaryEntry AddGlossary(string source, string target, string category)
    {
        source = source.Trim();
        target = target.Trim();
        category = category.Trim();
        if (source.Length == 0 || target.Length == 0)
            throw new InvalidOperationException("术语原文和译文不能为空。");
        if (source.Length > 500 || target.Length > 500 || category.Length > 50)
            throw new InvalidOperationException("术语内容过长。");

        var entry = _database.AddGlossary(source, target, category);
        Interlocked.Increment(ref _glossaryRevision);
        return entry;
    }

    public void DeleteGlossary(long id)
    {
        if (_database.DeleteGlossary(id)) Interlocked.Increment(ref _glossaryRevision);
    }

    internal static TranslatorConfig ApplyRequestedLanguages(
        TranslatorConfig config,
        string? sourceLanguage,
        string? targetLanguage) =>
        config with
        {
            SourceLanguage = TranslatorConfig.NormalizeSourceLanguage(
                string.IsNullOrWhiteSpace(sourceLanguage) ? config.SourceLanguage : sourceLanguage),
            TargetLanguage = TranslatorConfig.NormalizeTargetLanguage(
                string.IsNullOrWhiteSpace(targetLanguage) ? config.TargetLanguage : targetLanguage)
        };

    private TranslatorConfig ResolveAndValidateConfig(TranslatorConfig candidate) =>
        ValidateConfig(candidate, _config.ApiKey);

    internal static TranslatorConfig ValidateConfig(TranslatorConfig candidate, string storedApiKey)
    {
        var apiKey = (string.IsNullOrWhiteSpace(candidate.ApiKey) ? storedApiKey : candidate.ApiKey).Trim();
        var config = candidate with
        {
            BaseUrl = candidate.BaseUrl.Trim(),
            ApiKey = apiKey,
            Model = candidate.Model.Trim(),
            SourceLanguage = TranslatorConfig.NormalizeSourceLanguage(candidate.SourceLanguage),
            TargetLanguage = TranslatorConfig.NormalizeTargetLanguage(candidate.TargetLanguage)
        };

        if (config.BaseUrl.Length > 2048) throw new InvalidOperationException("Base URL 不能超过 2048 个字符。");
        _ = OpenAiCompatibleClient.GetChatEndpoint(config.BaseUrl);
        if (config.ApiKey.Length == 0) throw new InvalidOperationException("请输入 API Key。");
        if (config.ApiKey.Length > 4096) throw new InvalidOperationException("API Key 过长。");
        if (config.Model.Length is 0 or > 200) throw new InvalidOperationException("模型名称不能为空且不能超过 200 个字符。");
        if (config.TimeoutSeconds is < 5 or > 120)
            throw new InvalidOperationException("超时时间必须在 5 到 120 秒之间。");
        return config;
    }

    private async Task<TranslationResult> QueueTranslationAsync(
        string cacheKey,
        string cacheIdentity,
        string text,
        TranslatorConfig config,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<TranslationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Increment(ref _queueLength);
        if (!_queue.Writer.TryWrite(new TranslationWorkItem(cacheKey, cacheIdentity, text, config, completion, cancellationToken)))
        {
            Interlocked.Decrement(ref _queueLength);
            throw new InvalidOperationException("翻译队列已满，请稍后重试。");
        }

        return await completion.Task;
    }

    private async Task RunWorkerAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync())
        {
            Interlocked.Decrement(ref _queueLength);
            try
            {
                work.CancellationToken.ThrowIfCancellationRequested();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(work.CancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(work.Config.TimeoutSeconds));
                var glossary = _database.LoadGlossary();
                if (glossary.Count > TranslationDatabase.MaxGlossaryEntries)
                    throw new InvalidOperationException($"术语表超过 {TranslationDatabase.MaxGlossaryEntries} 条，请先删除多余术语。");
                var glossaryPrompt = glossary.Count == 0
                    ? ""
                    : "\nUse these glossary mappings exactly:\n" +
                      string.Join('\n', glossary.Select(item => $"{item.Source} => {item.Target}"));
                var systemPrompt =
                    $"Translate from {work.Config.SourceLanguage} to {work.Config.TargetLanguage}. " +
                    $"Return only the translated text, with no explanation.{glossaryPrompt}";
                var translated = await OpenAiCompatibleClient.SendAsync(work.Config, work.Text, systemPrompt, timeout.Token);
                await _database.WriteCacheAsync(work.CacheKey, work.CacheIdentity, translated, timeout.Token);
                AddMemoryCache(work.CacheIdentity, translated);
                work.Completion.TrySetResult(new TranslationResult(translated, TranslationSource.Api, 0));
            }
            catch (OperationCanceledException) when (work.CancellationToken.IsCancellationRequested)
            {
                work.Completion.TrySetCanceled(work.CancellationToken);
            }
            catch (Exception exception)
            {
                work.Completion.TrySetException(exception);
            }
        }
    }

    private static string BuildCacheIdentity(string text, TranslatorConfig config, long glossaryRevision) =>
        JsonSerializer.Serialize(new[]
        {
            text,
            config.SourceLanguage,
            config.TargetLanguage,
            config.BaseUrl,
            config.Model,
            PromptVersion.ToString(),
            glossaryRevision.ToString()
        });

    private void AddMemoryCache(string key, string value)
    {
        lock (_memoryCacheLock)
        {
            var bytes = EstimateMemoryCacheBytes(key, value);
            if (bytes > MaxMemoryCacheBytes) return;
            if (_memoryCache.TryGetValue(key, out var oldValue))
                _memoryCacheBytes -= EstimateMemoryCacheBytes(key, oldValue);
            // ponytail: 超预算时整体清空；命中率不足时再换严格 LRU。
            if (_memoryCacheBytes + bytes > MaxMemoryCacheBytes)
            {
                _memoryCache.Clear();
                _memoryCacheBytes = 0;
            }
            _memoryCache[key] = value;
            _memoryCacheBytes += bytes;
        }
    }

    internal static long EstimateMemoryCacheBytes(string key, string value) =>
        64L + (key.Length + value.Length) * sizeof(char);

    private TranslationResult Complete(string text, TranslationSource source, long started, bool cacheHit)
    {
        var elapsed = (long)Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        Interlocked.Increment(ref _completedCount);
        if (cacheHit) Interlocked.Increment(ref _cacheHitCount);
        Interlocked.Exchange(ref _lastLatencyMs, elapsed);
        return new TranslationResult(text, source, elapsed);
    }

    private void RemoveInflight(string key, SharedTranslation shared)
    {
        if (_inflight.TryGetValue(key, out var current) && ReferenceEquals(current, shared))
            _inflight.TryRemove(key, out _);
    }

    internal static TranslationDatabase OpenDatabaseWithRecovery(string path, out string? warning)
    {
        try
        {
            warning = null;
            return new TranslationDatabase(path);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidDataException)
        {
            SqliteConnection.ClearAllPools();
            var suffix = $".backup-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(file)) File.Move(file, file + suffix);
            warning = $"原翻译数据库无法读取，已保留备份并建立新库：{exception.Message}";
            return new TranslationDatabase(path);
        }
    }

    internal sealed class SharedTranslation : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Lazy<Task<TranslationResult>> _task;
        private int _waiters;

        public Task<TranslationResult> Task => _task.Value;

        public SharedTranslation(
            Func<CancellationToken, Task<TranslationResult>> work,
            Action<SharedTranslation> completed)
        {
            _task = new Lazy<Task<TranslationResult>>(
                async () =>
                {
                    try { return await work(_cancellation.Token); }
                    finally { completed(this); }
                },
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public void AddWaiter() => Interlocked.Increment(ref _waiters);

        public bool RemoveWaiter() => Interlocked.Decrement(ref _waiters) == 0 && !Task.IsCompleted;

        public void Cancel() => _cancellation.Cancel();

        public void Dispose() => _cancellation.Dispose();
    }

    private sealed record TranslationWorkItem(
        string CacheKey,
        string CacheIdentity,
        string Text,
        TranslatorConfig Config,
        TaskCompletionSource<TranslationResult> Completion,
        CancellationToken CancellationToken);
}
