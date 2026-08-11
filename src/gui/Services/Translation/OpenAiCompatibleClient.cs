using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GameTranslator.Gui.Services;

internal static class OpenAiCompatibleClient
{
    private const int MaxResponseBytes = 1024 * 1024;
    private static readonly ConcurrentDictionary<string, byte> StandardOnlyEndpoints = new();
    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public static Task<string> SendAsync(
        TranslatorConfig config,
        string userText,
        string systemPrompt,
        CancellationToken cancellationToken) =>
        SendAsync(HttpClient, config, userText, systemPrompt, cancellationToken);

    internal static Task<string> SendAsync(
        HttpClient httpClient,
        TranslatorConfig config,
        string userText,
        string systemPrompt,
        CancellationToken cancellationToken) =>
        SendContentAsync(httpClient, config, userText, systemPrompt, cancellationToken);

    internal static async Task<string[]> SendBatchAsync(
        HttpClient httpClient,
        TranslatorConfig config,
        IReadOnlyList<string> texts,
        string systemPrompt,
        CancellationToken cancellationToken)
    {
        var content = await SendContentAsync(
            httpClient,
            config,
            JsonSerializer.Serialize(texts),
            systemPrompt + "\n输入是 JSON 字符串数组；仅输出同等长度、顺序一致的 JSON 译文数组。",
            cancellationToken);
        var translations = JsonSerializer.Deserialize<string[]>(content)
            ?? throw new InvalidDataException("API 未返回译文数组。");
        if (translations.Length != texts.Count || Array.Exists(translations, string.IsNullOrWhiteSpace))
            throw new InvalidDataException("API 返回的译文数量不一致或包含空白译文。");
        return translations;
    }

    public static Task<string[]> SendBatchAsync(
        TranslatorConfig config,
        IReadOnlyList<string> texts,
        string systemPrompt,
        CancellationToken cancellationToken) =>
        SendBatchAsync(HttpClient, config, texts, systemPrompt, cancellationToken);

    private static async Task<string> SendContentAsync(
        HttpClient httpClient,
        TranslatorConfig config,
        string userText,
        string systemPrompt,
        CancellationToken cancellationToken)
    {
        var endpoint = GetChatEndpoint(config.BaseUrl);
        var endpointKey = $"{endpoint.AbsoluteUri}\n{config.Model}";
        var includeThinking = !StandardOnlyEndpoints.ContainsKey(endpointKey);
        var confirmedFallback = false;
        var messages = new[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userText }
        };

        for (var attempt = 0; attempt < 3;)
        {
            try
            {
                object payload = includeThinking
                    ? new { model = config.Model, messages, thinking = new { type = "disabled" } }
                    : new { model = config.Model, messages };
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
                request.Content = JsonContent.Create(payload);
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var body = await ReadLimitedAsync(response.Content, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    if (confirmedFallback) StandardOnlyEndpoints.TryAdd(endpointKey, 0);
                    using var document = JsonDocument.Parse(body);
                    if (document.RootElement.TryGetProperty("choices", out var choices)
                        && choices.GetArrayLength() > 0
                        && choices[0].TryGetProperty("message", out var message)
                        && message.TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(content.GetString()))
                    {
                        return content.GetString()!.Trim();
                    }
                    throw new InvalidDataException("API 返回成功，但没有可用的译文。");
                }

                if (response.StatusCode == HttpStatusCode.BadRequest && includeThinking)
                {
                    includeThinking = false;
                    confirmedFallback = true;
                    continue;
                }

                var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                if (!retryable || attempt == 2)
                    throw new HttpRequestException($"API 请求失败：{(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);

                await Task.Delay(GetRetryDelay(response.Headers.RetryAfter, attempt), cancellationToken);
                attempt++;
            }
            catch (HttpRequestException exception) when (exception.StatusCode is null && attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken);
                attempt++;
            }
        }

        throw new InvalidOperationException("API 请求失败。");
    }

    internal static Uri GetChatEndpoint(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Base URL 格式无效。");
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            throw new InvalidOperationException("Base URL 必须使用 HTTPS；仅本机地址允许 HTTP。");
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException("Base URL 不能包含查询参数或片段。");

        var value = uri.AbsoluteUri.TrimEnd('/');
        return value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? new Uri(value)
            : new Uri($"{value}/chat/completions");
    }

    internal static TimeSpan GetRetryDelay(RetryConditionHeaderValue? retryAfter, int attempt)
    {
        var delay = retryAfter?.Delta ?? retryAfter?.Date - DateTimeOffset.UtcNow;
        return delay.HasValue && delay.Value > TimeSpan.Zero
            ? delay.Value
            : TimeSpan.FromMilliseconds(250 * (attempt + 1));
    }

    internal static async Task<byte[]> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxResponseBytes)
            throw new InvalidDataException("API 响应超过 1 MiB 限制。");

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) return output.ToArray();
            if (output.Length + count > MaxResponseBytes)
                throw new InvalidDataException("API 响应超过 1 MiB 限制。");
            output.Write(buffer, 0, count);
        }
    }
}
