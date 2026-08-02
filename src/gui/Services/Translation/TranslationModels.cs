namespace GameTranslator.Gui.Services;

public sealed record TranslatorConfig(
    string BaseUrl,
    string ApiKey,
    string Model,
    string SourceLanguage,
    string TargetLanguage,
    int TimeoutSeconds)
{
    public static TranslatorConfig Empty { get; } = new("", "", "", "自动检测", "简体中文", 30);
}

public enum TranslationSource { Api, MemoryCache, PersistentCache }

public sealed record TranslationResult(string Text, TranslationSource Source, long ElapsedMilliseconds);

public sealed record TranslationMetrics(
    long RequestCount,
    long CompletedCount,
    long CacheHitCount,
    long LastLatencyMilliseconds,
    int QueueLength);

public sealed record GlossaryEntry(long Id, string Source, string Target, string Category);
