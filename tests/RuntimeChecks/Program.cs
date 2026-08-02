using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http.Headers;
using System.Threading;
using GameTranslator.Gui.Services;
using Microsoft.Data.Sqlite;

var config = new TranslatorConfig("https://example.com/v1", "key", "model", "日语", "简体中文", 30);
var requestConfig = TranslationRuntime.ApplyRequestedLanguages(config, "ja", "zh-CN");
Check(requestConfig.SourceLanguage == "ja" && requestConfig.TargetLanguage == "zh-CN", "桥接语言覆盖失败");

var query = XUnityBridgeServer.ParseQuery("?from=ja&to=zh-CN&text=hello+world");
Check(query["from"] == "ja" && query["to"] == "zh-CN" && query["text"] == "hello world", "桥接查询解析失败");

var serverDelay = OpenAiCompatibleClient.GetRetryDelay(new RetryConditionHeaderValue(TimeSpan.FromSeconds(2)), 0);
Check(serverDelay == TimeSpan.FromSeconds(2), "Retry-After 秒数未生效");

var fallbackDelay = OpenAiCompatibleClient.GetRetryDelay(new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(-1)), 1);
Check(fallbackDelay == TimeSpan.FromMilliseconds(500), "过期 Retry-After 未回退");
Check(typeof(INotifyPropertyChanged).IsAssignableFrom(typeof(XUnityBridgeServer)), "桥接状态缺少变更通知");

var testDirectory = Path.Combine(Path.GetTempPath(), $"GameTranslator-{Guid.NewGuid():N}");
Directory.CreateDirectory(testDirectory);
try
{
    var database = new TranslationDatabase(Path.Combine(testDirectory, "checks.db"));
    await database.WriteCacheAsync("cache-key", "source", "translated", config, CancellationToken.None);
    Check(await database.ReadCacheAsync("cache-key", CancellationToken.None) == "translated", "持久缓存读写失败");
    for (var index = 0; index < TranslationDatabase.MaxGlossaryEntries; index++)
        database.AddGlossary($"source-{index}", $"target-{index}", "test");
    var rejected = false;
    try
    {
        database.AddGlossary("overflow", "overflow", "test");
    }
    catch (InvalidOperationException)
    {
        rejected = true;
    }
    Check(rejected, "术语上限未生效");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(testDirectory, true);
}

Console.WriteLine("RuntimeChecks: 7/7 passed");

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
