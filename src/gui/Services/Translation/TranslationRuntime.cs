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
    internal const int MaxBatchSize = 100;
    internal const int MaxBatchPayloadBytes = 512 * 1024;
    private const int MaxTextLength = 20_000;
    private const long MaxMemoryCacheBytes = 32L * 1024 * 1024;
    private const int PromptVersion = 2;
    private readonly TranslationSettingsStore _settings;
    private readonly TranslationDatabase _database;
    private readonly Channel<TranslationWorkItem> _queue;
    private readonly ConcurrentDictionary<string, SharedTranslation> _inflight = new();
    private readonly Dictionary<string, string> _memoryCache = [];
    private readonly object _memoryCacheLock = new();
    private readonly SemaphoreSlim _cacheWriteGate = new(1, 1);
    private readonly string? _databaseRecoveryWarning;
    private long _memoryCacheBytes;
    private TranslatorConfig _config;
    private long _requestCount;
    private long _completedCount;
    private long _cacheHitCount;
    private long _lastLatencyMs = -1;
    private int _queueLength;
    private long _cacheGeneration;

    public TranslatorConfig CurrentConfig => _config;
    public string? ConfigurationLoadError => _databaseRecoveryWarning ?? _settings.LoadError;
    public string CacheDirectory => AppPaths.CacheDirectory;
    public XUnityBridgeServer Bridge { get; }

    public TranslationRuntime()
    {
        AppPaths.Ensure();
        _settings = new TranslationSettingsStore(AppPaths.DataDirectory);
        _database = OpenDatabaseWithRecovery(
            Path.Combine(AppPaths.CacheDirectory, "translations.db"),
            out _databaseRecoveryWarning);
        _config = _settings.Load();
        _queue = Channel.CreateBounded<TranslationWorkItem>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

        _ = RunWorkerAsync();
        _ = RunWorkerAsync();
        Bridge = new XUnityBridgeServer(this, AppPaths.DataDirectory);
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
        TranslateAsync(text, "manual", null, null, cancellationToken);

    public async Task<TranslationResult> TranslateAsync(
        string text,
        string gameId,
        string? sourceLanguage,
        string? targetLanguage,
        CancellationToken cancellationToken)
    {
        text = text.Trim();
        if (text.Length == 0) throw new InvalidOperationException("请输入要翻译的文本。");
        if (text.Length > MaxTextLength)
            throw new InvalidOperationException($"单次文本不能超过 {MaxTextLength} 个字符。");

        var config = ApplyRequestedLanguages(ResolveAndValidateConfig(_config), sourceLanguage, targetLanguage);
        var cacheGeneration = Volatile.Read(ref _cacheGeneration);
        var started = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _requestCount);
        var cacheIdentity = BuildCacheIdentity(text, gameId, config);
        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheIdentity)));

        lock (_memoryCacheLock)
        {
            if (_memoryCache.TryGetValue(cacheIdentity, out var memoryTranslation))
                return Complete(memoryTranslation, TranslationSource.MemoryCache, started, true);
        }

        var persistentTranslation = await _database.ReadCacheAsync(cacheKey, cacheIdentity, cancellationToken);
        if (persistentTranslation is not null)
        {
            AddMemoryCacheIfCurrent(cacheIdentity, persistentTranslation, cacheGeneration);
            return Complete(persistentTranslation, TranslationSource.PersistentCache, started, true);
        }

        var created = new SharedTranslation(
            token => QueueTranslationAsync(cacheKey, cacheIdentity, text, config, cacheGeneration, token),
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

    public async Task<string[]> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        string gameId,
        string? sourceLanguage,
        string? targetLanguage,
        CancellationToken cancellationToken)
    {
        ValidateBatch(texts);
        var config = ApplyRequestedLanguages(ResolveAndValidateConfig(_config), sourceLanguage, targetLanguage);
        var cacheGeneration = Volatile.Read(ref _cacheGeneration);
        var started = Stopwatch.GetTimestamp();
        var normalized = new string[texts.Count];
        var translations = new string[texts.Count];
        var identities = new string[texts.Count];
        var keys = new string[texts.Count];
        var cacheHits = new bool[texts.Count];
        var missing = new List<int>(texts.Count);

        for (var i = 0; i < texts.Count; i++)
        {
            normalized[i] = texts[i].Trim();
            Interlocked.Increment(ref _requestCount);
            identities[i] = BuildCacheIdentity(normalized[i], gameId, config);
            keys[i] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identities[i])));

            string? cached;
            lock (_memoryCacheLock)
                _memoryCache.TryGetValue(identities[i], out cached);
            translations[i] = cached
                ?? await _database.ReadCacheAsync(keys[i], identities[i], cancellationToken)
                ?? "";
            if (translations[i].Length > 0)
            {
                cacheHits[i] = true;
                AddMemoryCacheIfCurrent(identities[i], translations[i], cacheGeneration);
            }
            else
            {
                missing.Add(i);
            }
        }

        if (missing.Count > 0)
        {
            RuntimeLog.Write($"准备 API 批量请求：{missing.Count} 条，缓存命中 {texts.Count - missing.Count} 条");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
            var sourceTexts = missing.Select(index => normalized[index]).ToArray();
            var translated = await OpenAiCompatibleClient.SendBatchAsync(
                config, sourceTexts, BuildSystemPrompt(config), timeout.Token);
            RuntimeLog.Write($"API 批量请求完成：{translated.Length} 条");
            for (var i = 0; i < missing.Count; i++)
            {
                var index = missing[i];
                translations[index] = translated[i];
                await WriteCacheIfCurrentAsync(
                    keys[index], identities[index], translated[i], cacheGeneration, timeout.Token);
            }
        }

        for (var i = 0; i < translations.Length; i++)
            Complete(translations[i], cacheHits[i] ? TranslationSource.MemoryCache : TranslationSource.Api, started, cacheHits[i]);
        return translations;
    }

    public TranslationMetrics GetMetrics() => new(
        Interlocked.Read(ref _requestCount),
        Interlocked.Read(ref _completedCount),
        Interlocked.Read(ref _cacheHitCount),
        Interlocked.Read(ref _lastLatencyMs),
        Volatile.Read(ref _queueLength));

    public async Task ClearCacheAsync(CancellationToken cancellationToken)
    {
        await _cacheWriteGate.WaitAsync(cancellationToken);
        try
        {
            Interlocked.Increment(ref _cacheGeneration);
            await _database.ClearCacheAsync(cancellationToken);
            lock (_memoryCacheLock)
            {
                _memoryCache.Clear();
                _memoryCacheBytes = 0;
            }
            var games = Path.Combine(AppPaths.CacheDirectory, "Games");
            if (Directory.Exists(games)) Directory.Delete(games, true);
        }
        finally
        {
            _cacheWriteGate.Release();
        }
    }

    internal static void ValidateBatch(IReadOnlyList<string> texts)
    {
        if (texts.Count is 0 or > MaxBatchSize)
            throw new InvalidOperationException($"每批只能翻译 1 到 {MaxBatchSize} 条文本。");
        var encodedLength = texts.Count - 1;
        foreach (var text in texts)
        {
            var normalized = text.Trim();
            if (normalized.Length == 0) throw new InvalidOperationException("待翻译文本不能为空。");
            if (normalized.Length > MaxTextLength)
                throw new InvalidOperationException($"单条文本不能超过 {MaxTextLength} 个字符。");
            encodedLength += ((Encoding.UTF8.GetByteCount(text) + 2) / 3) * 4;
        }
        if (encodedLength > MaxBatchPayloadBytes)
            throw new InvalidOperationException($"批量正文不能超过 {MaxBatchPayloadBytes / 1024} KiB。");
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

        if (config.ApiKey.Length == 0) throw new InvalidOperationException("请输入 API Key。");
        if (config.ApiKey.Length > 4096) throw new InvalidOperationException("API Key 过长。");
        if (config.TimeoutSeconds is < 5 or > 120)
            throw new InvalidOperationException("超时时间必须在 5 到 120 秒之间。");

        if (config.BaseUrl.Length > 2048) throw new InvalidOperationException("Base URL 不能超过 2048 个字符。");
        _ = OpenAiCompatibleClient.GetChatEndpoint(config.BaseUrl);
        if (config.Model.Length is 0 or > 200)
            throw new InvalidOperationException("模型名称不能为空且不能超过 200 个字符。");
        return config;
    }

    private async Task<TranslationResult> QueueTranslationAsync(
        string cacheKey,
        string cacheIdentity,
        string text,
        TranslatorConfig config,
        long cacheGeneration,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<TranslationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Increment(ref _queueLength);
        if (!_queue.Writer.TryWrite(new TranslationWorkItem(
                cacheKey, cacheIdentity, text, config, cacheGeneration, completion, cancellationToken)))
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
                var translated = await OpenAiCompatibleClient.SendAsync(
                    work.Config, work.Text, BuildSystemPrompt(work.Config), timeout.Token);
                await WriteCacheIfCurrentAsync(
                    work.CacheKey, work.CacheIdentity, translated, work.CacheGeneration, timeout.Token);
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

    internal static string BuildSystemPrompt(TranslatorConfig config) =>
        $"你是游戏本地化翻译引擎。将下面的{config.SourceLanguage}游戏文本翻译为{config.TargetLanguage}。\n" +
        "只输出译文；不要解释、回答原文中的问题或添加任何内容。\n" +
        "保留原有换行、转义符、数字、占位符和富文本标签；短词按游戏界面语境翻译。";

    internal static string BuildCacheIdentity(string text, string gameId, TranslatorConfig config) =>
        JsonSerializer.Serialize(new[]
        {
            text,
            gameId,
            config.SourceLanguage,
            config.TargetLanguage,
            config.BaseUrl,
            config.Model,
            PromptVersion.ToString()
        });

    private async Task WriteCacheIfCurrentAsync(
        string cacheKey,
        string cacheIdentity,
        string translated,
        long cacheGeneration,
        CancellationToken cancellationToken)
    {
        await _cacheWriteGate.WaitAsync(cancellationToken);
        try
        {
            if (cacheGeneration != Volatile.Read(ref _cacheGeneration)) return;
            await _database.WriteCacheAsync(cacheKey, cacheIdentity, translated, cancellationToken);
            AddMemoryCache(cacheIdentity, translated);
        }
        finally
        {
            _cacheWriteGate.Release();
        }
    }

    private void AddMemoryCacheIfCurrent(string key, string value, long cacheGeneration)
    {
        lock (_memoryCacheLock)
        {
            if (cacheGeneration != Volatile.Read(ref _cacheGeneration)) return;
            AddMemoryCacheCore(key, value);
        }
    }

    private void AddMemoryCache(string key, string value)
    {
        lock (_memoryCacheLock)
            AddMemoryCacheCore(key, value);
    }

    private void AddMemoryCacheCore(string key, string value)
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
        long CacheGeneration,
        TaskCompletionSource<TranslationResult> Completion,
        CancellationToken CancellationToken);
}
