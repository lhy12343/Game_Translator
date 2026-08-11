using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameTranslator.Gui.Services;

namespace GameTranslator.Gui.ViewModels;

public partial class HomePageViewModel : ViewModelBase
{
    private const string BepInExUrl = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip";
    private const string BepInExHash = "82F9878551030F54657792C0740D9D51A09500EEAE1FBA21106B0C441E6732C4";
    private const string XUnityUrl = "https://github.com/bbepis/XUnity.AutoTranslator/releases/download/v5.6.1/XUnity.AutoTranslator-BepInEx-5.6.1.zip";
    private const string XUnityHash = "FBB7D1BBE2C7CC168DA6DCCBC500FB74786A85A548F52495C8A1592AC46407F5";
    private const string FontUrl = "https://github.com/bbepis/XUnity.AutoTranslator/releases/download/v5.5.0/TMP_Font_AssetBundles_2025-12-08.7z";
    private const string FontHash = "889E963FB9DBD4B64927E0ADF5D9060E1D0FB9D6BCEB0C407D0597643E2B54EC";
    private static readonly HttpClient InstallerClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly TranslationRuntime _runtime;
    private readonly string _gamePathFile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchGameCommand))]
    private string _gamePath = "";

    [ObservableProperty]
    private string _gameName = "未选择游戏";

    [ObservableProperty]
    private string _selectedProcessId = "-";

    [ObservableProperty]
    private string _monitorStatus = "等待选择游戏";

    public int? ActiveProcessId { get; private set; }
    public int? RunningProcessId
    {
        get
        {
            if (ActiveProcessId is not int processId) return null;
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.HasExited) return processId;
            }
            catch (ArgumentException) { }
            ActiveProcessId = FindProcessIdAtPath(GamePath);
            SelectedProcessId = ActiveProcessId?.ToString() ?? "-";
            return ActiveProcessId;
        }
    }
    public bool IsGameRunning => RunningProcessId.HasValue;
    public XUnityBridgeServer GameBridge => _runtime.Bridge;

    public HomePageViewModel(TranslationRuntime runtime)
    {
        _runtime = runtime;
        _gamePathFile = Path.Combine(AppPaths.DataDirectory, "game-path.txt");

        try
        {
            if (File.Exists(_gamePathFile)) SelectGame(File.ReadAllText(_gamePathFile));
        }
        catch (IOException exception)
        {
            MonitorStatus = $"读取游戏路径失败：{exception.Message}";
        }
    }

    public void SelectGame(string path)
    {
        try
        {
            path = Path.GetFullPath(path.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            MonitorStatus = $"游戏路径无效：{exception.Message}";
            return;
        }
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            MonitorStatus = "请选择有效的游戏 EXE。";
            return;
        }

        GamePath = path;
        GameName = Path.GetFileNameWithoutExtension(path);
        ActiveProcessId = FindProcessIdAtPath(path);
        SelectedProcessId = ActiveProcessId?.ToString() ?? "-";
        MonitorStatus = ActiveProcessId.HasValue
            ? "已检测到游戏正在运行"
            : File.Exists(GetXUnityConfigPath(path))
                ? "已就绪：可启动并翻译"
                : "首次启动时将自动安装翻译组件";

        try
        {
            File.WriteAllText(_gamePathFile, path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MonitorStatus = $"保存游戏路径失败：{exception.Message}";
        }
    }

    private bool CanLaunchGame() => File.Exists(GamePath);

    [RelayCommand(CanExecute = nameof(CanLaunchGame))]
    private async Task LaunchGameAsync()
    {
        try
        {
            if (IsGameRunning)
            {
                MonitorStatus = "游戏已经在运行";
                return;
            }
            var configPath = GetXUnityConfigPath(GamePath);
            var fontFile = await EnsureXUnityAsync(GamePath, configPath);

            var config = File.ReadAllText(configPath);
            config = SetIniValue(config, "Service", "Endpoint", "CustomTranslate");
            config = SetIniValue(config, "Service", "FallbackEndpoint", "");
            config = SetIniValue(config, "Custom", "Url", GameBridge.GetUrl(GetGameId(GamePath)));
            config = SetIniValue(config, "General", "Language", "zh-CN");
            config = SetIniValue(config, "General", "FromLanguage",
                _runtime.CurrentConfig.SourceLanguage == TranslatorConfig.Japanese ? "ja" : "en");
            config = SetIniValue(config, "Behaviour", "OverrideFont", "Microsoft YaHei");
            config = SetIniValue(config, "Behaviour", "FallbackFontTextMeshPro", fontFile);
            config = SetIniValue(config, "Behaviour", "EnableBatching", "True");
            var outputFile = GetGameCacheFile(GamePath);
            MoveLegacyGameCache(GamePath, outputFile.Replace("{Lang}", "zh-CN", StringComparison.Ordinal));
            config = SetIniValue(config, "Files", "OutputFile", outputFile);
            File.WriteAllText(configPath, config);
            GameBridge.Start();

            var process = Process.Start(new ProcessStartInfo(GamePath)
            {
                WorkingDirectory = Path.GetDirectoryName(GamePath)!,
                UseShellExecute = true
            });
            if (process is null) throw new InvalidOperationException("系统未返回游戏进程。");

            ActiveProcessId = process.Id;
            SelectedProcessId = process.Id.ToString();
            MonitorStatus = "翻译桥接已写入，游戏已启动";
            process.Dispose();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException or Win32Exception or InvalidOperationException)
        {
            MonitorStatus = $"启动失败：{exception.Message}";
        }
    }

    private async Task<string> EnsureXUnityAsync(string gamePath, string configPath)
    {
        var gameDirectory = Path.GetDirectoryName(gamePath)!;
        var managedDirectory = Path.Combine(
            gameDirectory, Path.GetFileNameWithoutExtension(gamePath) + "_Data", "Managed");
        if (!Directory.Exists(managedDirectory))
            throw new InvalidOperationException("当前仅支持 Unity Mono x64 游戏，未检测到 Managed 目录。");
        var unityPlayer = Path.Combine(gameDirectory, "UnityPlayer.dll");
        if (!File.Exists(unityPlayer))
            throw new InvalidOperationException("未检测到 UnityPlayer.dll。");
        var fontFile = GetFontFile(FileVersionInfo.GetVersionInfo(unityPlayer).FileMajorPart);

        MonitorStatus = "首次运行：正在安装翻译组件…";
        if (!File.Exists(Path.Combine(gameDirectory, "BepInEx", "core", "BepInEx.dll")))
            await InstallPackageAsync(BepInExUrl, BepInExHash, gameDirectory);
        if (!File.Exists(Path.Combine(gameDirectory, "BepInEx", "plugins", "XUnity.AutoTranslator", "XUnity.AutoTranslator.Plugin.Core.dll")))
            await InstallPackageAsync(XUnityUrl, XUnityHash, gameDirectory);
        InstallBatchEndpoint(gameDirectory);
        if (!File.Exists(Path.Combine(gameDirectory, fontFile)))
            await InstallFontAsync(gameDirectory, fontFile);
        var oldFont = Path.Combine(gameDirectory, "arialuni_sdf_u2019");
        if (fontFile != "arialuni_sdf_u2019" && File.Exists(oldFont)) File.Delete(oldFont);

        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        if (!File.Exists(configPath)) File.WriteAllText(configPath, "");
        return fontFile;
    }

    internal static string GetFontFile(int unityMajor) => unityMajor switch
    {
        >= 6000 => "arialuni_sdf_u6000",
        >= 2022 => "arialuni_sdf_u2022",
        2021 => "arialuni_sdf_u2021",
        >= 2019 => "arialuni_sdf_u2019",
        2018 => "arialuni_sdf_u2018",
        >= 5 => "arialuni_sdf-u55to2017",
        _ => throw new InvalidOperationException("无法识别该游戏的 Unity 版本。")
    };

    internal static string GetGameId(string gamePath)
    {
        var normalizedPath = Path.GetFullPath(gamePath).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)))[..12];
        return $"{Path.GetFileNameWithoutExtension(gamePath)}-{hash}";
    }

    internal static string GetGameCacheFile(string gamePath) => Path.Combine(
        AppPaths.CacheDirectory,
        "Games",
        GetGameId(gamePath),
        "{Lang}",
        "Text",
        "_AutoGeneratedTranslations.txt");

    private static void MoveLegacyGameCache(string gamePath, string destination)
    {
        var source = Path.Combine(
            Path.GetDirectoryName(gamePath)!, "BepInEx", "Translation", "zh-CN", "Text", "_AutoGeneratedTranslations.txt");
        if (!File.Exists(source) || File.Exists(destination)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(source, destination);
    }

    private static async Task InstallPackageAsync(string url, string expectedHash, string destination)
    {
        var temporaryFile = await DownloadPackageAsync(url, expectedHash);
        try
        {
            ZipFile.ExtractToDirectory(temporaryFile, destination, true);
        }
        finally
        {
            File.Delete(temporaryFile);
        }
    }

    private static async Task InstallFontAsync(string destination, string fontFile)
    {
        var temporaryFile = await DownloadPackageAsync(FontUrl, FontHash);
        try
        {
            var startInfo = new ProcessStartInfo("tar.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-xf");
            startInfo.ArgumentList.Add(temporaryFile);
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(destination);
            startInfo.ArgumentList.Add(fontFile);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 Windows 字体解压工具。");
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0 || !File.Exists(Path.Combine(destination, fontFile)))
                throw new InvalidDataException($"字体解压失败：{await error}");
        }
        finally
        {
            File.Delete(temporaryFile);
        }
    }

    private static void InstallBatchEndpoint(string gameDirectory)
    {
        using var resource = typeof(HomePageViewModel).Assembly.GetManifestResourceStream("CustomTranslate.dll")
            ?? throw new InvalidDataException("批量翻译组件未打包。");
        using var output = new MemoryStream();
        resource.CopyTo(output);
        var payload = output.ToArray();
        var target = Path.Combine(gameDirectory, "BepInEx", "plugins", "XUnity.AutoTranslator", "Translators", "CustomTranslate.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!File.Exists(target) || !File.ReadAllBytes(target).AsSpan().SequenceEqual(payload))
            File.WriteAllBytes(target, payload);
    }

    private static int? FindProcessIdAtPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase)) return process.Id;
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return null;
    }

    private static async Task<string> DownloadPackageAsync(string url, string expectedHash)
    {
        var temporaryFile = Path.GetTempFileName();
        try
        {
            using var response = await InstallerClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var output = File.Create(temporaryFile))
                await response.Content.CopyToAsync(output);

            using var input = File.OpenRead(temporaryFile);
            if (!Convert.ToHexString(SHA256.HashData(input)).Equals(expectedHash, StringComparison.Ordinal))
                throw new InvalidDataException("翻译组件校验失败，已停止安装。");
            return temporaryFile;
        }
        catch
        {
            File.Delete(temporaryFile);
            throw;
        }
    }

    private static string GetXUnityConfigPath(string gamePath) =>
        Path.Combine(Path.GetDirectoryName(gamePath)!, "BepInEx", "config", "AutoTranslatorConfig.ini");

    internal static string SetIniValue(string text, string section, string key, string value)
    {
        var lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
        var header = $"[{section}]";
        var start = lines.FindIndex(line => string.Equals(line.Trim(), header, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Length != 0) lines.Add("");
            lines.Add(header);
            lines.Add($"{key}={value}");
            return string.Join(Environment.NewLine, lines);
        }

        var end = lines.FindIndex(start + 1, line => line.TrimStart().StartsWith('['));
        if (end < 0) end = lines.Count;
        for (var index = start + 1; index < end; index++)
        {
            var separator = lines[index].IndexOf('=');
            if (separator < 0 || !string.Equals(lines[index][..separator].Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;
            lines[index] = $"{key}={value}";
            return string.Join(Environment.NewLine, lines);
        }

        lines.Insert(end, $"{key}={value}");
        return string.Join(Environment.NewLine, lines);
    }
}
