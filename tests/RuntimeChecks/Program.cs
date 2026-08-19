using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using GameTranslator.Gui.Services;
using GameTranslator.Gui.ViewModels;
using GameTranslator.Gui.Views;
using Microsoft.Data.Sqlite;

var passed = 0;
var config = new TranslatorConfig("https://example.com/v1", "key", "model", "日语", "简体中文", 30);
var requestConfig = TranslationRuntime.ApplyRequestedLanguages(config, "ja", "zh");
Check(requestConfig.SourceLanguage == TranslatorConfig.Japanese && requestConfig.TargetLanguage == TranslatorConfig.Chinese, "桥接语言覆盖失败");
Check(TranslatorConfig.SupportedSourceLanguages.SequenceEqual([TranslatorConfig.English, TranslatorConfig.Japanese]), "源语言范围错误");
Check(Throws<InvalidOperationException>(() => TranslationRuntime.ValidateConfig(config with { SourceLanguage = "韩语" }, "")), "不支持的源语言未被拒绝");
Check(TranslationRuntime.EstimateMemoryCacheBytes("abc", "中文") == 74, "内存缓存字节估算错误");
Check(OpenAiCompatibleClient.GetChatEndpoint(config.BaseUrl).AbsoluteUri == "https://example.com/v1/chat/completions", "API 地址拼接失败");
Check(Throws<InvalidOperationException>(() => OpenAiCompatibleClient.GetChatEndpoint("http://example.com/v1")), "非本机 HTTP 地址未被拒绝");

var storedKeyConfig = TranslationRuntime.ValidateConfig(config with { ApiKey = "" }, "stored-key");
Check(storedKeyConfig.ApiKey == "stored-key", "已保存密钥未被沿用");
Check(Throws<InvalidOperationException>(() => TranslationRuntime.ValidateConfig(config with { Model = new string('x', 201) }, "")), "过长模型名称未被拒绝");
var prompt = TranslationRuntime.BuildSystemPrompt(config);
Check(prompt.Contains("你是游戏本地化翻译引擎")
      && prompt.Contains("不要解释、回答原文中的问题"),
    "中文翻译提示词错误");
Check(TranslationRuntime.BuildCacheIdentity("Start", "game-a", config)
      != TranslationRuntime.BuildCacheIdentity("Start", "game-b", config),
    "SQLite 缓存键未包含游戏身份");
Check(TranslationRuntime.BuildCacheIdentity("a\u001Fb", "c", config)
      != TranslationRuntime.BuildCacheIdentity("a", "b\u001Fc", config),
    "SQLite 缓存身份字段边界发生碰撞");

var query = XUnityBridgeServer.ParseQuery("?from=ja&to=zh&text=hello+world");
Check(query["from"] == "ja" && query["to"] == "zh" && query["text"] == "hello world", "桥接查询解析失败");
var batchTexts = new[] { "Start", "第一行\n第二行", "<color=red>HP 10</color>" };
Check(XUnityBridgeServer.DecodeBatch(XUnityBridgeServer.EncodeBatch(batchTexts)).SequenceEqual(batchTexts),
    "桥接批量文本往返失败");
var hundredTexts = Enumerable.Range(0, TranslationRuntime.MaxBatchSize).Select(index => $"Text {index}").ToArray();
Check(XUnityBridgeServer.DecodeBatch(XUnityBridgeServer.EncodeBatch(hundredTexts)).SequenceEqual(hundredTexts),
    "百条批量文本往返失败");

var serverDelay = OpenAiCompatibleClient.GetRetryDelay(new RetryConditionHeaderValue(TimeSpan.FromSeconds(2)), 0);
Check(serverDelay == TimeSpan.FromSeconds(2), "Retry-After 秒数未生效");
var fallbackDelay = OpenAiCompatibleClient.GetRetryDelay(new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(-1)), 1);
Check(fallbackDelay == TimeSpan.FromMilliseconds(500), "过期 Retry-After 未回退");
Check(typeof(INotifyPropertyChanged).IsAssignableFrom(typeof(XUnityBridgeServer)), "桥接状态缺少变更通知");
Check(typeof(HomePageViewModel).Assembly.GetManifestResourceStream("CustomTranslate.dll") is not null, "批量组件未嵌入发布包");
Check(Path.GetFullPath(AppPaths.CacheDirectory).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase),
    "缓存目录不在软件根目录");
var firstGameCache = HomePageViewModel.GetGameCacheFile(@"D:\Games\First\Game.exe");
var secondGameCache = HomePageViewModel.GetGameCacheFile(@"D:\Games\Second\Game.exe");
Check(firstGameCache != secondGameCache
      && Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(firstGameCache))))
          ?.StartsWith("Game-", StringComparison.OrdinalIgnoreCase) == true,
    "同名游戏缓存未按完整路径隔离");
var runtime = new TranslationRuntime();
Check(runtime.Bridge.Status == "未启动", "未选择游戏时桥接不应自动运行");
var currentProcessHome = new HomePageViewModel(runtime);
currentProcessHome.SelectGame(Environment.ProcessPath!);
Check(currentProcessHome.IsGameRunning, "应用重启后未按 EXE 路径识别运行中的进程");
Exception? monitorError = null;
var monitorThread = new Thread(() =>
{
    try
    {
        var page = new MonitorPage { DataContext = new MonitorPageViewModel(new HomePageViewModel(runtime), runtime) };
        page.Measure(new Size(1000, 800));
        page.Arrange(new Rect(0, 0, 1000, 800));
        page.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
    catch (Exception exception)
    {
        monitorError = exception;
    }
});
monitorThread.SetApartmentState(ApartmentState.STA);
monitorThread.Start();
monitorThread.Join();
Check(monitorError is null, $"性能监控页面加载失败：{monitorError?.Message}");
var gameCacheTest = Path.Combine(runtime.CacheDirectory, "Games", "RuntimeChecks", "cache.txt");
Directory.CreateDirectory(Path.GetDirectoryName(gameCacheTest)!);
File.WriteAllText(gameCacheTest, "cached");
await runtime.ClearCacheAsync(CancellationToken.None);
Check(!Directory.Exists(Path.Combine(runtime.CacheDirectory, "Games")), "清除缓存后游戏译文目录仍然存在");
Check(HomePageViewModel.GetFontFile(6000) == "arialuni_sdf_u6000"
      && HomePageViewModel.GetFontFile(2022) == "arialuni_sdf_u2022"
      && HomePageViewModel.GetFontFile(2021) == "arialuni_sdf_u2021"
      && HomePageViewModel.GetFontFile(2020) == "arialuni_sdf_u2019"
      && HomePageViewModel.GetFontFile(2018) == "arialuni_sdf_u2018"
      && HomePageViewModel.GetFontFile(2017) == "arialuni_sdf-u55to2017",
    "Unity 版本字体分类错误");

var tmpLegacyOverride2022 = HomePageViewModel.GetTmpFontSettings("TMP 主字体覆盖（u2022）", 2022);
var tmpFallback2022 = HomePageViewModel.GetTmpFontSettings("TMP 回退字体（u2022）", 2022);
var tmpAuto2022 = HomePageViewModel.GetTmpFontSettings("自动（按 Unity 版本）", 2022);
var tmpDisabled = HomePageViewModel.GetTmpFontSettings("不注入中文字体", 2022);
Check(tmpLegacyOverride2022.FontFile == "arialuni_sdf_u2022"
      && tmpLegacyOverride2022.FallbackFontFile == "arialuni_sdf_u2022"
      && tmpLegacyOverride2022.OverrideFontFile is null
      && tmpFallback2022.FallbackFontFile == "arialuni_sdf_u2022"
      && tmpFallback2022.OverrideFontFile is null
      && tmpAuto2022.FallbackFontFile == "arialuni_sdf_u2022"
      && tmpAuto2022.OverrideFontFile is null
      && tmpDisabled.FontFile is null
      && tmpDisabled.FallbackFontFile is null
      && tmpDisabled.OverrideFontFile is null,
    "TMP 字体模式配置错误");

var xunityUrl = (string)typeof(HomePageViewModel).GetField("XUnityUrl", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
var xunityHash = (string)typeof(HomePageViewModel).GetField("XUnityHash", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
var xunityVersion = (string)typeof(HomePageViewModel).GetField("XUnityVersion", BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;
Check(xunityUrl == "https://github.com/lhy12343/XUnity.AutoTranslator/releases/download/v5.6.2/XUnity.AutoTranslator-BepInEx-5.6.2.zip"
      && xunityHash == "6506170D7DF23924A76399FAE63D12CA21895ADFA9BF22AF8606342172D81F39"
      && xunityVersion == "5.6.2.0",
    "XUnity 修复版下载源配置错误");

var ini = HomePageViewModel.SetIniValue("[Service]\nEndpoint=GoogleTranslate\n\n[General]\nLanguage=zh", "Service", "Endpoint", "CustomTranslate");
ini = HomePageViewModel.SetIniValue(ini, "Custom", "Url", "http://127.0.0.1/translate/token");
ini = HomePageViewModel.SetIniValue(ini, "General", "Language", "zh-CN");
ini = HomePageViewModel.SetIniValue(ini, "General", "FromLanguage", "ja");
ini = HomePageViewModel.SetIniValue(ini, "Behaviour", "FallbackFontTextMeshPro", "arialuni_sdf_u6000");
ini = HomePageViewModel.SetIniValue(ini, "Behaviour", "EnableBatching", "True");
Check(ini.Contains("[Service]") && ini.Contains("Endpoint=CustomTranslate")
      && ini.Contains("[General]") && ini.Contains("Language=zh-CN") && ini.Contains("FromLanguage=ja")
      && ini.Contains("FallbackFontTextMeshPro=arialuni_sdf_u6000")
      && ini.Contains("EnableBatching=True")
      && ini.Contains("[Custom]") && ini.Contains("Url=http://127.0.0.1/translate/token"),
    "XUnity 配置更新失败");

string? requestBody = null;
AuthenticationHeaderValue? authorization = null;
var retryHandler = new StubHandler(async (attempt, request, token) =>
{
    requestBody = await request.Content!.ReadAsStringAsync(token);
    authorization = request.Headers.Authorization;
    return attempt < 3
        ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
        : JsonResponse("translated");
});
using (var client = new HttpClient(retryHandler))
{
    var translated = await OpenAiCompatibleClient.SendAsync(client, config, "source", "system", CancellationToken.None);
    Check(translated == "translated" && retryHandler.Count == 3, "5xx 有限重试失败");
    Check(authorization?.Scheme == "Bearer" && authorization.Parameter == "key"
          && requestBody?.Contains("\"model\":\"model\"") == true
          && requestBody.Contains("\"role\":\"system\"")
          && requestBody.Contains("\"role\":\"user\""), "OpenAI 兼容请求格式错误");
}

var badRequestHandler = new StubHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
using (var client = new HttpClient(badRequestHandler))
{
    Check(await ThrowsAsync<HttpRequestException>(() => OpenAiCompatibleClient.SendAsync(client, config, "source", "system", CancellationToken.None))
          && badRequestHandler.Count == 1, "不可重试请求被重复发送");
}

var invalidJsonHandler = new StubHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new StringContent("not-json")
}));
using (var client = new HttpClient(invalidJsonHandler))
    Check(await ThrowsAsync<System.Text.Json.JsonException>(() => OpenAiCompatibleClient.SendAsync(client, config, "source", "system", CancellationToken.None)), "非法 JSON 未被拒绝");

var thinkingHandler = new StubHandler(async (_, request, _) =>
{
    var body = await request.Content!.ReadAsStringAsync();
    return body.Contains("\"thinking\":{\"type\":\"disabled\"}", StringComparison.Ordinal)
        ? new HttpResponseMessage(HttpStatusCode.BadRequest)
        : JsonResponse("translated");
});
using (var client = new HttpClient(thinkingHandler))
{
    Check(await OpenAiCompatibleClient.SendAsync(client, config, "source", "system", CancellationToken.None) == "translated"
          && thinkingHandler.Count == 2,
        "严格 OpenAI 接口未在拒绝 thinking 后兼容降级");
    Check(await OpenAiCompatibleClient.SendAsync(client, config, "source", "system", CancellationToken.None) == "translated"
          && thinkingHandler.Count == 3,
        "已确认不支持 thinking 的接口仍被重复探测");
}

var batchHandler = new StubHandler((_, _, _) => Task.FromResult(JsonResponse("[\\\"开始\\\",\\\"退出\\\"]")));
using (var client = new HttpClient(batchHandler))
{
    var translated = await OpenAiCompatibleClient.SendBatchAsync(
        client, config, ["Start", "Exit"], "system", CancellationToken.None);
    Check(translated.SequenceEqual(["开始", "退出"]) && batchHandler.Count == 1,
        "批量翻译未保持顺序或产生了多次 API 请求");
}

var malformedBatchHandler = new StubHandler((attempt, _, _) => Task.FromResult(
    attempt == 1
        ? JsonResponse("[\\\"截断")
        : attempt == 2 ? JsonResponse("开始") : JsonResponse("退出")));
using (var client = new HttpClient(malformedBatchHandler))
{
    var translated = await OpenAiCompatibleClient.SendBatchAsync(
        client, config, ["Start", "Exit"], "system", CancellationToken.None);
    Check(translated.SequenceEqual(["开始", "退出"]) && malformedBatchHandler.Count == 3,
        "批量 JSON 损坏时未回退到单条翻译");
}

string? singleRequestBody = null;
var singleHandler = new StubHandler(async (_, request, _) =>
{
    singleRequestBody = await request.Content!.ReadAsStringAsync();
    return JsonResponse("开始");
});
using (var client = new HttpClient(singleHandler))
{
    var translated = await OpenAiCompatibleClient.SendBatchAsync(
        client, config, ["Start"], "system", CancellationToken.None);
    Check(translated.SequenceEqual(["开始"])
          && singleHandler.Count == 1
          && singleRequestBody?.Contains("\"content\":\"Start\"", StringComparison.Ordinal) == true,
        "单条翻译未绕过 JSON 数组协议");
}

var cancellationHandler = new StubHandler(async (_, _, token) =>
{
    await Task.Delay(Timeout.InfiniteTimeSpan, token);
    return JsonResponse("unreachable");
});
using (var client = new HttpClient(cancellationHandler))
using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20)))
    Check(await ThrowsAsync<OperationCanceledException>(() => OpenAiCompatibleClient.SendAsync(client, config, "source", "system", cancellation.Token)), "API 取消未生效");

var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using (var shared = new TranslationRuntime.SharedTranslation(
           async token =>
           {
               using var registration = token.Register(cancellationObserved.SetResult);
               await Task.Delay(Timeout.InfiniteTimeSpan, token);
               return new TranslationResult("unreachable", TranslationSource.Api, 0);
           },
           _ => { }))
{
    shared.AddWaiter();
    shared.AddWaiter();
    var sharedTask = shared.Task;
    Check(!shared.RemoveWaiter() && !cancellationObserved.Task.IsCompleted, "单个合并请求取消影响了其他等待者");
    Check(shared.RemoveWaiter(), "最后一个等待者取消后未停止共享请求");
    shared.Cancel();
    await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
    Check(await ThrowsAsync<OperationCanceledException>(() => sharedTask), "共享 API 请求未真正取消");
}

var testDirectory = Path.Combine(Path.GetTempPath(), $"GameTranslator-{Guid.NewGuid():N}");
Directory.CreateDirectory(testDirectory);
try
{
    var legacyDirectory = Path.Combine(testDirectory, "legacy");
    var migratedDataDirectory = Path.Combine(testDirectory, "data");
    var migratedCacheDirectory = Path.Combine(testDirectory, "cache");
    Directory.CreateDirectory(legacyDirectory);
    Directory.CreateDirectory(migratedDataDirectory);
    Directory.CreateDirectory(migratedCacheDirectory);
    File.WriteAllText(Path.Combine(legacyDirectory, "translations.db"), "legacy-main");
    File.WriteAllText(Path.Combine(legacyDirectory, "translations.db-wal"), "legacy-wal");
    File.WriteAllText(Path.Combine(migratedCacheDirectory, "translations.db"), "current-main");
    AppPaths.MigrateLegacy(legacyDirectory, migratedDataDirectory, migratedCacheDirectory);
    Check(File.Exists(Path.Combine(legacyDirectory, "translations.db"))
          && File.Exists(Path.Combine(legacyDirectory, "translations.db-wal"))
          && !File.Exists(Path.Combine(migratedCacheDirectory, "translations.db-wal")),
        "目标主库存在时仍拆分迁移了旧 SQLite 文件组");

    var oversizedBatch = Enumerable.Repeat(new string('a', 4_000), TranslationRuntime.MaxBatchSize).ToArray();
    Check(Throws<InvalidOperationException>(() => TranslationRuntime.ValidateBatch(oversizedBatch)),
        "超过桥接正文上限的批次未被统一拒绝");

    var databasePath = Path.Combine(testDirectory, "checks.db");
    await using (var legacy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()))
    {
        await legacy.OpenAsync();
        await using var command = legacy.CreateCommand();
        command.CommandText = """
            CREATE TABLE translation_cache (
                cache_key TEXT PRIMARY KEY,
                original_text TEXT NOT NULL,
                translated_text TEXT NOT NULL,
                source_language TEXT NOT NULL,
                target_language TEXT NOT NULL,
                model TEXT NOT NULL,
                created_utc TEXT NOT NULL
            )
            """;
        await command.ExecuteNonQueryAsync();
    }

    var database = new TranslationDatabase(databasePath);
    await database.WriteCacheAsync("cache-key", "identity-a", "translated", CancellationToken.None);
    Check(await database.ReadCacheAsync("cache-key", "identity-a", CancellationToken.None) == "translated", "持久缓存读写失败");
    Check(await database.ReadCacheAsync("cache-key", "identity-b", CancellationToken.None) is null, "缓存 Hash 碰撞防护失败");
    var reopenedDatabase = new TranslationDatabase(databasePath);
    Check(await reopenedDatabase.ReadCacheAsync("cache-key", "identity-a", CancellationToken.None) == "translated", "重启后持久缓存未命中");
    await reopenedDatabase.ClearCacheAsync(CancellationToken.None);
    Check(await reopenedDatabase.ReadCacheAsync("cache-key", "identity-a", CancellationToken.None) is null, "清除缓存未删除持久译文");

    var budgetDatabase = new TranslationDatabase(Path.Combine(testDirectory, "budget.db"), 600);
    var longTranslation = new string('中', 100);
    await budgetDatabase.WriteCacheAsync("budget-1", "identity-1", longTranslation, CancellationToken.None);
    await budgetDatabase.WriteCacheAsync("budget-2", "identity-2", longTranslation, CancellationToken.None);
    Check(await budgetDatabase.ReadCacheAsync("budget-1", "identity-1", CancellationToken.None) is null
          && await budgetDatabase.ReadCacheAsync("budget-2", "identity-2", CancellationToken.None) == longTranslation,
        "SQLite 字节预算淘汰失败");

    await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('translation_cache') WHERE name IN ('original_text', 'source_language', 'target_language', 'model', 'created_utc')";
        Check((long)(await command.ExecuteScalarAsync() ?? -1L) == 0, "缓存表仍包含重复字段");
        command.CommandText = "PRAGMA user_version";
        Check((long)(await command.ExecuteScalarAsync() ?? 0L) == 4, "数据库 Schema 迁移失败");
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'glossary'";
        Check((long)(await command.ExecuteScalarAsync() ?? -1L) == 0, "术语数据表未删除");
        command.CommandText = "PRAGMA page_size";
        var pageSize = (long)(await command.ExecuteScalarAsync() ?? 0L);
        command.CommandText = "PRAGMA max_page_count";
        Check(pageSize * (long)(await command.ExecuteScalarAsync() ?? long.MaxValue) <= 256L * 1024 * 1024,
            "SQLite 主库未设置物理上限");
    }

    var newerDatabasePath = Path.Combine(testDirectory, "newer.db");
    await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = newerDatabasePath }.ToString()))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version = 5";
        await command.ExecuteNonQueryAsync();
    }
    Check(Throws<InvalidDataException>(() => new TranslationDatabase(newerDatabasePath)), "更高版本数据库未被安全拒绝");
    _ = TranslationRuntime.OpenDatabaseWithRecovery(newerDatabasePath, out var recoveryWarning);
    Check(recoveryWarning is not null
          && Directory.EnumerateFiles(testDirectory, "newer.db.backup-*").Any(),
        "不可读数据库未被备份恢复");

    var settings = new TranslationSettingsStore(testDirectory);
    settings.Save(config with { ApiKey = "secret-check-key" });
    Check(!File.ReadAllText(Path.Combine(testDirectory, "config.json")).Contains("secret-check-key"), "API Key 出现在明文配置中");
    Check(!File.Exists(Path.Combine(testDirectory, "api-key.bin")), "仍在使用非原子的独立密钥文件");
    Check(settings.Load().ApiKey == "secret-check-key", "加密保存的 API Key 无法读取");

}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(testDirectory, true);
}

Console.WriteLine($"RuntimeChecks: {passed}/{passed} passed");

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    passed++;
}

static bool Throws<TException>(Action action) where TException : Exception
{
    try
    {
        action();
        return false;
    }
    catch (TException)
    {
        return true;
    }
}

static async Task<bool> ThrowsAsync<TException>(Func<Task> action) where TException : Exception
{
    try
    {
        await action();
        return false;
    }
    catch (TException)
    {
        return true;
    }
}

static HttpResponseMessage JsonResponse(string content) => new(HttpStatusCode.OK)
{
    Content = new StringContent($"{{\"choices\":[{{\"message\":{{\"content\":\"{content}\"}}}}]}}", Encoding.UTF8, "application/json")
};

sealed class StubHandler(Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    private int _count;
    public int Count => _count;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        send(Interlocked.Increment(ref _count), request, cancellationToken);
}
