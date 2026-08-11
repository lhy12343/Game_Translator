using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GameTranslator.Gui.Services;

public sealed class XUnityBridgeServer : INotifyPropertyChanged
{
    private const int Port = 52731;
    private const int MaxConcurrentClients = 32;
    private const int MaxRequestBodyBytes = TranslationRuntime.MaxBatchPayloadBytes;
    private readonly TranslationRuntime _runtime;
    private readonly string _token;
    private readonly SemaphoreSlim _clientSlots = new(MaxConcurrentClients);
    private string _status = "未启动";

    public string Url => $"http://127.0.0.1:{Port}/translate/{_token}";
    public string GetUrl(string gameId) => $"{Url}/{Uri.EscapeDataString(gameId)}";
    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public XUnityBridgeServer(TranslationRuntime runtime, string dataDirectory)
    {
        _runtime = runtime;
        _token = GetOrCreateToken(Path.Combine(dataDirectory, "bridge-token.txt"));
    }

    public void Start()
    {
        if (Status != "未启动") return;
        Status = "正在启动";
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();
            Status = "运行中";

            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                if (!_clientSlots.Wait(0))
                {
                    try
                    {
                        using (client)
                        await using (var stream = client.GetStream())
                            await WriteResponseAsync(stream, 503, "Busy", CancellationToken.None);
                    }
                    catch
                    {
                        client.Dispose();
                    }
                    continue;
                }
                _ = HandleClientAsync(client);
            }
        }
        catch (Exception exception)
        {
            Status = $"启动失败：{exception.Message}";
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                    var request = await ReadRequestAsync(stream, timeout.Token);
                    var uri = new Uri($"http://127.0.0.1{request.Target}");

                    var pathPrefix = $"/translate/{_token}/";
                    if (!uri.AbsolutePath.StartsWith(pathPrefix, StringComparison.Ordinal)
                        || uri.AbsolutePath.Length == pathPrefix.Length)
                    {
                        await WriteResponseAsync(stream, 404, "Not Found", timeout.Token);
                        return;
                    }
                    var gameId = Uri.UnescapeDataString(uri.AbsolutePath[pathPrefix.Length..]);

                    var query = ParseQuery(uri.Query);
                    query.TryGetValue("from", out var sourceLanguage);
                    query.TryGetValue("to", out var targetLanguage);
                    if (request.Method == "POST")
                    {
                        var texts = DecodeBatch(request.Body);
                        RuntimeLog.Write($"批量请求：{texts.Length} 条，正文 {request.Body.Length} 字符");
                        if (texts.Length is 0 or > TranslationRuntime.MaxBatchSize || texts.Any(string.IsNullOrWhiteSpace))
                            throw new InvalidDataException($"Batch must contain 1 to {TranslationRuntime.MaxBatchSize} texts");
                        var translations = await _runtime.TranslateBatchAsync(texts, gameId, sourceLanguage, targetLanguage, timeout.Token);
                        RuntimeLog.Write($"批量完成：{translations.Length} 条");
                        await WriteResponseAsync(stream, 200, EncodeBatch(translations), timeout.Token);
                        return;
                    }

                    if (!query.TryGetValue("text", out var text) || string.IsNullOrWhiteSpace(text))
                    {
                        await WriteResponseAsync(stream, 400, "Missing text", timeout.Token);
                        return;
                    }

                    var result = await _runtime.TranslateAsync(text, gameId, sourceLanguage, targetLanguage, timeout.Token);
                    RuntimeLog.Write("单条请求完成");
                    await WriteResponseAsync(stream, 200, result.Text, timeout.Token);
                }
                catch
                {
                    try { await WriteResponseAsync(stream, 500, "Translation failed", CancellationToken.None); }
                    catch { }
                }
            }
        }
        finally
        {
            _clientSlots.Release();
        }
    }

    private static async Task<(string Method, string Target, string Body)> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var request = new MemoryStream();
        var buffer = new byte[4096];
        var headerEnd = -1;
        while (request.Length < 65_536)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) throw new InvalidDataException("Incomplete request");
            request.Write(buffer, 0, count);
            var bytes = request.GetBuffer().AsSpan(0, (int)request.Length);
            headerEnd = bytes.IndexOf("\r\n\r\n"u8);
            if (headerEnd >= 0) break;
        }
        if (headerEnd < 0) throw new InvalidDataException("Request headers are too long");

        var headerText = Encoding.ASCII.GetString(request.GetBuffer(), 0, headerEnd);
        var requestLine = headerText.Split("\r\n", 2, StringSplitOptions.None)[0];
        var parts = requestLine.Split(' ');
        if (parts.Length != 3 || parts[0] is not ("GET" or "POST"))
            throw new InvalidDataException("Only GET and POST are supported");

        var contentLength = 0;
        foreach (var line in headerText.Split("\r\n"))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && !int.TryParse(line[15..].Trim(), out contentLength))
                throw new InvalidDataException("Invalid Content-Length");
        if (contentLength is < 0 or > MaxRequestBodyBytes) throw new InvalidDataException("Request body is too long");

        var bodyOffset = headerEnd + 4;
        while (request.Length - bodyOffset < contentLength)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, contentLength - (int)(request.Length - bodyOffset))), cancellationToken);
            if (count == 0) throw new InvalidDataException("Incomplete request body");
            request.Write(buffer, 0, count);
        }
        return (parts[0], parts[1], Encoding.UTF8.GetString(request.GetBuffer(), bodyOffset, contentLength));
    }

    internal static string EncodeBatch(IEnumerable<string> texts) =>
        string.Join('\n', texts.Select(text => Convert.ToBase64String(Encoding.UTF8.GetBytes(text))));

    internal static string[] DecodeBatch(string body) =>
        body.Split('\n').Select(value => Encoding.UTF8.GetString(Convert.FromBase64String(value.TrimEnd('\r')))).ToArray();

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            values[WebUtility.UrlDecode(pair[0])] = pair.Length == 2 ? WebUtility.UrlDecode(pair[1]) : "";
        }
        return values;
    }

    private static async Task WriteResponseAsync(Stream stream, int statusCode, string body, CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var reason = statusCode switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", 503 => "Service Unavailable", _ => "Internal Server Error" };
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(bodyBytes, cancellationToken);
    }

    private static string GetOrCreateToken(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length == 32) return existing;
        }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        File.WriteAllText(path, token);
        return token;
    }
}
