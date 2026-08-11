using System;
using System.Collections.Generic;

namespace GameTranslator.Gui.Services;

public sealed record TranslatorConfig(
    string BaseUrl,
    string ApiKey,
    string Model,
    string SourceLanguage,
    string TargetLanguage,
    int TimeoutSeconds)
{
    public const string English = "英语";
    public const string Japanese = "日语";
    public const string Chinese = "简体中文";
    public static IReadOnlyList<string> SupportedSourceLanguages { get; } = [English, Japanese];
    public static TranslatorConfig Empty { get; } = new("", "", "", English, Chinese, 30);

    public static string NormalizeSourceLanguage(string language)
    {
        language = language.Trim();
        if (language.Equals(English, StringComparison.OrdinalIgnoreCase)
            || language.Equals("en", StringComparison.OrdinalIgnoreCase)
            || language.StartsWith("en-", StringComparison.OrdinalIgnoreCase)) return English;
        if (language.Equals(Japanese, StringComparison.OrdinalIgnoreCase)
            || language.Equals("ja", StringComparison.OrdinalIgnoreCase)
            || language.Equals("jp", StringComparison.OrdinalIgnoreCase)
            || language.StartsWith("ja-", StringComparison.OrdinalIgnoreCase)) return Japanese;
        throw new InvalidOperationException("源语言仅支持英语或日语。");
    }

    public static string NormalizeTargetLanguage(string language)
    {
        language = language.Trim();
        if (language.Equals(Chinese, StringComparison.OrdinalIgnoreCase)
            || language.Equals("zh", StringComparison.OrdinalIgnoreCase)
            || language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
            || language.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase)) return Chinese;
        throw new InvalidOperationException("目标语言固定为简体中文。");
    }
}

public enum TranslationSource { Api, MemoryCache, PersistentCache }

public sealed record TranslationResult(string Text, TranslationSource Source, long ElapsedMilliseconds);

public sealed record TranslationMetrics(
    long RequestCount,
    long CompletedCount,
    long CacheHitCount,
    long LastLatencyMilliseconds,
    int QueueLength);
