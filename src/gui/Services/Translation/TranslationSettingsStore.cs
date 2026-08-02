using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameTranslator.Gui.Services;

internal sealed class TranslationSettingsStore
{
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("GameTranslator.ApiKey.v1");
    private readonly string _configPath;
    private readonly string _apiKeyPath;

    public string? LoadError { get; private set; }

    public TranslationSettingsStore(string dataDirectory)
    {
        _configPath = Path.Combine(dataDirectory, "config.json");
        _apiKeyPath = Path.Combine(dataDirectory, "api-key.bin");
    }

    public TranslatorConfig Load()
    {
        if (!File.Exists(_configPath)) return TranslatorConfig.Empty;

        try
        {
            var stored = JsonSerializer.Deserialize<StoredConfig>(File.ReadAllText(_configPath))
                         ?? throw new InvalidDataException("配置文件为空。");
            if (stored.BaseUrl is null || stored.Model is null || stored.SourceLanguage is null || stored.TargetLanguage is null)
                throw new InvalidDataException("配置文件缺少必要字段。");

            var apiKey = !string.IsNullOrWhiteSpace(stored.ApiKeyCiphertext)
                ? UnprotectApiKey(Convert.FromBase64String(stored.ApiKeyCiphertext))
                : File.Exists(_apiKeyPath) ? UnprotectApiKey(File.ReadAllBytes(_apiKeyPath)) : "";
            return new TranslatorConfig(
                stored.BaseUrl,
                apiKey,
                stored.Model,
                stored.SourceLanguage == TranslatorConfig.Japanese ? TranslatorConfig.Japanese : TranslatorConfig.English,
                TranslatorConfig.Chinese,
                stored.TimeoutSeconds);
        }
        catch (Exception exception) when (exception is JsonException or IOException or CryptographicException or InvalidDataException or FormatException)
        {
            LoadError = $"本地配置读取失败：{exception.Message}";
            return TranslatorConfig.Empty;
        }
    }

    public void Save(TranslatorConfig config)
    {
        var plainKey = Encoding.UTF8.GetBytes(config.ApiKey);
        byte[]? protectedKey = null;

        try
        {
            protectedKey = ProtectedData.Protect(plainKey, DpapiEntropy, DataProtectionScope.CurrentUser);
            var stored = new StoredConfig(
                config.BaseUrl,
                config.Model,
                config.SourceLanguage,
                config.TargetLanguage,
                config.TimeoutSeconds,
                Convert.ToBase64String(protectedKey));
            var configJson = JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true });
            WriteAtomically(_configPath, Encoding.UTF8.GetBytes(configJson));
            if (File.Exists(_apiKeyPath)) File.Delete(_apiKeyPath);
            LoadError = null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainKey);
            if (protectedKey is not null) CryptographicOperations.ZeroMemory(protectedKey);
        }
    }

    private static string UnprotectApiKey(byte[] protectedKey)
    {
        var plainKey = ProtectedData.Unprotect(protectedKey, DpapiEntropy, DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plainKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainKey);
        }
    }

    private static void WriteAtomically(string path, byte[] content)
    {
        var temporaryPath = path + ".tmp";
        File.WriteAllBytes(temporaryPath, content);
        File.Move(temporaryPath, path, true);
    }

    private sealed record StoredConfig(
        string? BaseUrl,
        string? Model,
        string? SourceLanguage,
        string? TargetLanguage,
        int TimeoutSeconds,
        string? ApiKeyCiphertext);
}
