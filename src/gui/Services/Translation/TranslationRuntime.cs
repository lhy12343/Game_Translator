using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace GameTranslator.Gui.Services;

public sealed class TranslationRuntime
{
    private const int MaxTextLength = 20_000;
    private const string PromptVersion = "1";
    private readonly TranslationSettingsStore _settings;
    private readonly TranslationDatabase _database;
    private readonly Channel<TranslationWorkItem> _queue;
    private readonly ConcurrentDictionary<string, Lazy<Task<TranslationResult>>> _inflight = new();
    private readonly Dictionary<string, string> _memoryCache = [];
    private readonly object _memoryCacheLock = new();
    private TranslatorConfig _config;
    private long _requestCount;
    private long _completedCount;
    private long _cacheHitCount;
    private long _lastLatencyMs = -1;
    private long _glossaryRevision;
    private int _queueLength;

    public TranslatorConfig CurrentConfig => _config;
    public string? ConfigurationLoadError => _settings.LoadError;
    public XUnityBridgeServer Bridge { get; }

    static TranslationRuntime()
    {
        Debug.Assert(OpenAiCompatibleClient.GetChatEndpoint("https://example.com/v1").AbsoluteUri == "https://example.com/v1/chat/completions");
        Debug.Assert(OpenAiCompatibleClient.GetChatEndpoint("http://localhost:1234/v1/chat/completions").AbsoluteUri == "http://localhost:1234/v1/chat/completions");
    }

    public TranslationRuntime()
    {
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameTranslator");
        Directory.CreateDirectory(dataDirectory);
        _settings = new TranslationSettingsStore(dataDirectory);
        _database = new TranslationDatabase(Path.Combine(dataDirectory, "translations.db"));
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
        var cacheKey = BuildCacheKey(text, config, Interlocked.Read(ref _glossaryRevision));

        lock (_memoryCacheLock)
        {
            if (_memoryCache.TryGetValue(cacheKey, out var memoryTranslation))
                return Complete(memoryTranslation, TranslationSource.MemoryCache, started, true);
        }

        var persistentTranslation = await _database.ReadCacheAsync(cacheKey, cancellationToken);
        if (persistentTranslation is not null)
        {
            AddMemoryCache(cacheKey, persistentTranslation);
            return Complete(persistentTranslation, TranslationSource.PersistentCache, started, true);
        }

        var lazy = _inflight.GetOrAdd(cacheKey, _ => new Lazy<Task<TranslationResult>>(
            () => QueueTranslationAsync(cacheKey, text, config),
            LazyThreadSafetyMode.ExecutionAndPublication));
        var result = await lazy.Value.WaitAsync(cancellationToken);
        return Complete(result.Text, result.Source, started, false);
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
            SourceLanguage = ValidateRequestedLanguage(sourceLanguage, config.SourceLanguage),
            TargetLanguage = ValidateRequestedLanguage(targetLanguage, config.TargetLanguage)
        };

    private TranslatorConfig ResolveAndValidateConfig(TranslatorConfig candidate)
    {
        var apiKey = string.IsNullOrWhiteSpace(candidate.ApiKey) ? _config.ApiKey : candidate.ApiKey.Trim();
        var config = candidate with
        {
            BaseUrl = candidate.BaseUrl.Trim(),
            ApiKey = apiKey,
            Model = candidate.Model.Trim(),
            SourceLanguage = candidate.SourceLanguage.Trim(),
            TargetLanguage = candidate.TargetLanguage.Trim()
        };

        _ = OpenAiCompatibleClient.GetChatEndpoint(config.BaseUrl);
        if (config.ApiKey.Length == 0) throw new InvalidOperationException("请输入 API Key。");
        if (config.Model.Length == 0) throw new InvalidOperationException("请输入模型名称。");
        if (config.SourceLanguage.Length == 0 || config.TargetLanguage.Length == 0)
            throw new InvalidOperationException("请选择源语言和目标语言。");
        if (config.TimeoutSeconds is < 5 or > 120)
            throw new InvalidOperationException("超时时间必须在 5 到 120 秒之间。");
        return config;
    }

    private static string ValidateRequestedLanguage(string? requested, string fallback)
    {
        if (string.IsNullOrWhiteSpace(requested)) return fallback;
        requested = requested.Trim();
        if (requested.Length > 32) throw new InvalidOperationException("语言代码不能超过 32 个字符。");
        return requested;
    }

    private async Task<TranslationResult> QueueTranslationAsync(string cacheKey, string text, TranslatorConfig config)
    {
        var completion = new TaskCompletionSource<TranslationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Increment(ref _queueLength);
        if (!_queue.Writer.TryWrite(new TranslationWorkItem(cacheKey, text, config, completion)))
        {
            Interlocked.Decrement(ref _queueLength);
            _inflight.TryRemove(cacheKey, out _);
            throw new InvalidOperationException("翻译队列已满，请稍后重试。");
        }

        try
        {
            return await completion.Task;
        }
        finally
        {
            _inflight.TryRemove(cacheKey, out _);
        }
    }

    private async Task RunWorkerAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync())
        {
            Interlocked.Decrement(ref _queueLength);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(work.Config.TimeoutSeconds));
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
                await _database.WriteCacheAsync(work.CacheKey, work.Text, translated, work.Config, timeout.Token);
                AddMemoryCache(work.CacheKey, translated);
                work.Completion.TrySetResult(new TranslationResult(translated, TranslationSource.Api, 0));
            }
            catch (Exception exception)
            {
                work.Completion.TrySetException(exception);
            }
        }
    }

    private static string BuildCacheKey(string text, TranslatorConfig config, long glossaryRevision)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            text,
            config.SourceLanguage,
            config.TargetLanguage,
            config.BaseUrl,
            config.Model,
            PromptVersion,
            glossaryRevision.ToString()
        });
        return Convert.ToHexString(SHA256.HashData(data));
    }

    private void AddMemoryCache(string key, string value)
    {
        lock (_memoryCacheLock)
        {
            // ponytail: 简单有界缓存；真实命中率不足时再换严格 LRU。
            if (_memoryCache.Count >= 1000) _memoryCache.Clear();
            _memoryCache[key] = value;
        }
    }

    private TranslationResult Complete(string text, TranslationSource source, long started, bool cacheHit)
    {
        var elapsed = (long)Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        Interlocked.Increment(ref _completedCount);
        if (cacheHit) Interlocked.Increment(ref _cacheHitCount);
        Interlocked.Exchange(ref _lastLatencyMs, elapsed);
        return new TranslationResult(text, source, elapsed);
    }

    private sealed record TranslationWorkItem(
        string CacheKey,
        string Text,
        TranslatorConfig Config,
        TaskCompletionSource<TranslationResult> Completion);
}
