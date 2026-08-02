using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GameTranslator.Gui.Services;
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

var query = XUnityBridgeServer.ParseQuery("?from=ja&to=zh&text=hello+world");
Check(query["from"] == "ja" && query["to"] == "zh" && query["text"] == "hello world", "桥接查询解析失败");

var serverDelay = OpenAiCompatibleClient.GetRetryDelay(new RetryConditionHeaderValue(TimeSpan.FromSeconds(2)), 0);
Check(serverDelay == TimeSpan.FromSeconds(2), "Retry-After 秒数未生效");
var fallbackDelay = OpenAiCompatibleClient.GetRetryDelay(new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(-1)), 1);
Check(fallbackDelay == TimeSpan.FromMilliseconds(500), "过期 Retry-After 未回退");
Check(typeof(INotifyPropertyChanged).IsAssignableFrom(typeof(XUnityBridgeServer)), "桥接状态缺少变更通知");

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

var badRequestHandler = new StubHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
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
        Check((long)(await command.ExecuteScalarAsync() ?? 0L) == 3, "数据库 Schema 迁移失败");
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
        command.CommandText = "PRAGMA user_version = 4";
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

    for (var index = 0; index < TranslationDatabase.MaxGlossaryEntries; index++)
        database.AddGlossary($"source-{index}", $"target-{index}", "test");
    Check(Throws<InvalidOperationException>(() => database.AddGlossary("overflow", "overflow", "test")), "术语上限未生效");
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
