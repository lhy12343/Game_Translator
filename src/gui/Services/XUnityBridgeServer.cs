using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GameTranslator.Gui.Services;

public sealed class XUnityBridgeServer : INotifyPropertyChanged
{
    private const int Port = 52731;
    private readonly TranslationRuntime _runtime;
    private readonly string _token;
    private string _status = "正在启动";

    public string Url => $"http://127.0.0.1:{Port}/translate/{_token}";
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
        using (client)
        await using (var stream = client.GetStream())
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                var target = await ReadRequestTargetAsync(stream, timeout.Token);
                var uri = new Uri($"http://127.0.0.1{target}");

                if (uri.AbsolutePath != $"/translate/{_token}")
                {
                    await WriteResponseAsync(stream, 404, "Not Found", timeout.Token);
                    return;
                }

                var query = ParseQuery(uri.Query);
                if (!query.TryGetValue("text", out var text) || string.IsNullOrWhiteSpace(text))
                {
                    await WriteResponseAsync(stream, 400, "Missing text", timeout.Token);
                    return;
                }

                query.TryGetValue("from", out var sourceLanguage);
                query.TryGetValue("to", out var targetLanguage);
                var result = await _runtime.TranslateAsync(text, sourceLanguage, targetLanguage, timeout.Token);
                await WriteResponseAsync(stream, 200, result.Text, timeout.Token);
            }
            catch (Exception exception)
            {
                try
                {
                    await WriteResponseAsync(stream, 500, $"Translation failed: {exception.Message}", CancellationToken.None);
                }
                catch
                {
                    // 客户端已断开，无需继续处理。
                }
            }
        }
    }

    private static async Task<string> ReadRequestTargetAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var request = new MemoryStream();
        var buffer = new byte[4096];
        while (request.Length < 65_536)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) throw new InvalidDataException("Incomplete request");
            request.Write(buffer, 0, count);
            var bytes = request.GetBuffer().AsSpan(0, (int)request.Length);
            if (bytes.IndexOf("\r\n\r\n"u8) >= 0) break;
        }
        if (request.Length >= 65_536) throw new InvalidDataException("Request headers are too long");

        var headerText = Encoding.ASCII.GetString(request.GetBuffer(), 0, (int)request.Length);
        var requestLine = headerText.Split("\r\n", 2, StringSplitOptions.None)[0];
        var parts = requestLine.Split(' ');
        if (parts.Length != 3 || parts[0] != "GET") throw new InvalidDataException("Only GET is supported");
        return parts[1];
    }

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
        var reason = statusCode switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", _ => "Internal Server Error" };
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
