using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameTranslator.Gui.Services;

namespace GameTranslator.Gui.ViewModels;

public partial class HomePageViewModel : ViewModelBase
{
    private const string BepInExUrl = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip";
    private const string BepInExHash = "82F9878551030F54657792C0740D9D51A09500EEAE1FBA21106B0C441E6732C4";
    private const string XUnityVersion = "5.6.2.0";
    private const string XUnityUrl = "https://github.com/lhy12343/XUnity.AutoTranslator/releases/download/v5.6.2/XUnity.AutoTranslator-BepInEx-5.6.2.zip";
    private const string XUnityHash = "6506170D7DF23924A76399FAE63D12CA21895ADFA9BF22AF8606342172D81F39";
    private const string FontUrl = "https://github.com/bbepis/XUnity.AutoTranslator/releases/download/v5.5.0/TMP_Font_AssetBundles_2025-12-08.7z";
    private const string FontHash = "889E963FB9DBD4B64927E0ADF5D9060E1D0FB9D6BCEB0C407D0597643E2B54EC";
    private static readonly HttpClient InstallerClient = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly TranslationRuntime _runtime;
    private readonly string _gamePathFile;
    private readonly string _fontSettingsFile;
    private readonly Dictionary<string, string> _fontModes = new(StringComparer.OrdinalIgnoreCase);
    private string _fontSettingsGameKey = "";

    /// <summary>每个游戏的 TMP 中文字体模式选项。</summary>
    public string[] TmpFontModes { get; } =
    [
        "自动（按 Unity 版本）",
        "TMP 回退字体（u2022）",
        "老版 TMP 兼容（u2019）",
        "不注入中文字体"
    ];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchGameCommand))]
    private string _gamePath = "";

    [ObservableProperty]
    private string _gameName = "未选择游戏";

    [ObservableProperty]
    private string _selectedProcessId = "-";

    [ObservableProperty]
    private string _monitorStatus = "等待选择游戏";

    [ObservableProperty]
    private string _tmpFontMode = "自动（按 Unity 版本）";

    partial void OnTmpFontModeChanged(string value)
    {
        if (_fontSettingsGameKey.Length == 0) return;
        _fontModes[_fontSettingsGameKey] = value;
        SaveTmpFontModes();
    }

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
        _fontSettingsFile = Path.Combine(AppPaths.DataDirectory, "font-modes.json");
        LoadTmpFontModes();

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
        _fontSettingsGameKey = GetGameId(path);
        TmpFontMode = _fontModes.TryGetValue(_fontSettingsGameKey, out var savedMode) ? savedMode : TmpFontModes[0];
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
            RuntimeLog.Write($"[启动] 准备启动游戏：{GamePath}");
            var configPath = GetXUnityConfigPath(GamePath);
            var fontSettings = await EnsureXUnityAsync(GamePath, configPath);
            RuntimeLog.Write($"[启动] 组件就绪，字体：{fontSettings.FontFile ?? "无（不注入中文字体）"}");

            var config = File.ReadAllText(configPath);
            config = SetIniValue(config, "Service", "Endpoint", "CustomTranslate");
            config = SetIniValue(config, "Service", "FallbackEndpoint", "");
            config = SetIniValue(config, "Custom", "Url", GameBridge.GetUrl(GetGameId(GamePath)));
            config = SetIniValue(config, "General", "Language", "zh-CN");
            config = SetIniValue(config, "General", "FromLanguage",
                _runtime.CurrentConfig.SourceLanguage == TranslatorConfig.Japanese ? "ja" : "en");
            config = SetIniValue(config, "Behaviour", "OverrideFont", "Microsoft YaHei");
            config = SetIniValue(config, "Behaviour", "FallbackFontTextMeshPro", fontSettings.FallbackFontFile ?? "");
            config = SetIniValue(config, "Behaviour", "OverrideFontTextMeshPro", fontSettings.OverrideFontFile ?? "");
            config = SetIniValue(config, "Behaviour", "EnableBatching", "True");
            var outputFile = GetGameCacheFile(GamePath);
            MoveLegacyGameCache(GamePath, outputFile.Replace("{Lang}", "zh-CN", StringComparison.Ordinal));
            config = SetIniValue(config, "Files", "OutputFile", outputFile);
            File.WriteAllText(configPath, config);
            RuntimeLog.Write($"[启动] 配置已写入：{configPath}");

            GameBridge.Start();
            RuntimeLog.Write($"[启动] 翻译桥接已启动：{GameBridge.Url}");

            var process = Process.Start(new ProcessStartInfo(GamePath)
            {
                WorkingDirectory = Path.GetDirectoryName(GamePath)!,
                UseShellExecute = true
            });
            if (process is null) throw new InvalidOperationException("系统未返回游戏进程。");

            ActiveProcessId = process.Id;
            SelectedProcessId = process.Id.ToString();
            MonitorStatus = "翻译桥接已写入，游戏已启动";
            RuntimeLog.Write($"[启动] 游戏进程已创建：PID={process.Id}");
            _ = WatchGameWindowAsync(process.Id);
            process.Dispose();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException or Win32Exception or InvalidOperationException)
        {
            MonitorStatus = $"启动失败：{exception.Message}";
            RuntimeLog.Write($"[启动] 启动失败：{exception}");
        }
    }

    /// <summary>后台监控游戏进程：记录窗口是否出现、进程是否退出、BepInEx 日志是否生成。</summary>
    private async Task WatchGameWindowAsync(int processId)
    {
        var gameDirectory = Path.GetDirectoryName(GamePath) ?? "";
        var bepinexLog = Path.Combine(gameDirectory, "BepInEx", "LogOutput.log");
        var bepinexLogSeen = File.Exists(bepinexLog);
        var startTick = Environment.TickCount64;
        var lastSnapshot = 0L;
        string? seenTitle = null;
        RuntimeLog.Write($"[监控] 开始监控游戏进程 PID={processId}（每 2 秒检测窗口 / 进程 / BepInEx 日志）");

        while (true)
        {
            await Task.Delay(2000);
            var elapsedSec = (Environment.TickCount64 - startTick) / 1000;

            Process? proc = null;
            try { proc = Process.GetProcessById(processId); } catch (ArgumentException) { }
            var alive = proc is not null && !proc.HasExited;
            proc?.Dispose();

            if (!bepinexLogSeen && File.Exists(bepinexLog))
            {
                bepinexLogSeen = true;
                RuntimeLog.Write($"[监控] {elapsedSec}s 检测到 BepInEx 日志文件生成（注入器已开始工作）：{bepinexLog}");
            }

            string visibleTitle = "";
            try
            {
                foreach (var (title, visible) in WindowObserver.FindWindows(processId))
                {
                    if (visible && !string.IsNullOrWhiteSpace(title))
                    {
                        visibleTitle = title;
                        break;
                    }
                }
            }
            catch { }

            if (!string.IsNullOrEmpty(visibleTitle) && visibleTitle != seenTitle)
            {
                seenTitle = visibleTitle;
                RuntimeLog.Write($"[监控] {elapsedSec}s 游戏窗口已出现：\"{visibleTitle}\"");
                return;
            }

            if (!alive)
            {
                RuntimeLog.Write($"[监控] {elapsedSec}s 游戏进程已退出（PID={processId}）。若窗口从未出现，请查看上方日志和 {bepinexLog}");
                return;
            }

            if (Environment.TickCount64 - lastSnapshot >= 10000)
            {
                lastSnapshot = Environment.TickCount64;
                var windowCount = 0;
                try { windowCount = WindowObserver.FindWindows(processId).Count; } catch { }
                RuntimeLog.Write($"[监控] {elapsedSec}s 进程存活，顶层窗口数={windowCount}，可见窗口=\"{seenTitle ?? "尚无"}\"，BepInEx 日志={(bepinexLogSeen ? "有" : "无")}");
            }

            if (elapsedSec >= 90 && seenTitle is null)
            {
                RuntimeLog.Write($"[监控] 警告：启动 90 秒后仍未出现游戏窗口。常见原因：BepInEx/XUnity 插件加载卡死或崩溃。请查看：{bepinexLog}");
                return;
            }
        }
    }

    private async Task<(string? FontFile, string? FallbackFontFile, string? OverrideFontFile)> EnsureXUnityAsync(string gamePath, string configPath)
    {
        var gameDirectory = Path.GetDirectoryName(gamePath)!;
        var managedDirectory = Path.Combine(
            gameDirectory, Path.GetFileNameWithoutExtension(gamePath) + "_Data", "Managed");
        if (!Directory.Exists(managedDirectory))
            throw new InvalidOperationException("当前仅支持 Unity Mono x64 游戏，未检测到 Managed 目录。");
        var unityPlayer = Path.Combine(gameDirectory, "UnityPlayer.dll");
        if (!File.Exists(unityPlayer))
            throw new InvalidOperationException("未检测到 UnityPlayer.dll。");
        var fontSettings = GetTmpFontSettings(
            TmpFontMode,
            FileVersionInfo.GetVersionInfo(unityPlayer).FileMajorPart);

        MonitorStatus = "首次运行：正在安装翻译组件…";
        if (!File.Exists(Path.Combine(gameDirectory, "BepInEx", "core", "BepInEx.dll")))
            await InstallPackageAsync(BepInExUrl, BepInExHash, gameDirectory);
        var xunityCore = Path.Combine(gameDirectory, "BepInEx", "plugins", "XUnity.AutoTranslator", "XUnity.AutoTranslator.Plugin.Core.dll");
        if (!File.Exists(xunityCore) || FileVersionInfo.GetVersionInfo(xunityCore).FileVersion != XUnityVersion)
            await InstallPackageAsync(XUnityUrl, XUnityHash, gameDirectory);
        InstallBatchEndpoint(gameDirectory);
        if (fontSettings.FontFile is not null && !File.Exists(Path.Combine(gameDirectory, fontSettings.FontFile)))
            await InstallFontAsync(gameDirectory, fontSettings.FontFile);
        var oldFont = Path.Combine(gameDirectory, "arialuni_sdf_u2019");
        if (fontSettings.FontFile != "arialuni_sdf_u2019" && File.Exists(oldFont)) File.Delete(oldFont);

        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        if (!File.Exists(configPath)) File.WriteAllText(configPath, "");
        return fontSettings;
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

    internal static (string? FontFile, string? FallbackFontFile, string? OverrideFontFile) GetTmpFontSettings(string mode, int unityMajor) => mode switch
    {
        "TMP 回退字体（u2022）" => ("arialuni_sdf_u2022", "arialuni_sdf_u2022", null),
        "TMP 主字体覆盖（u2022）" => ("arialuni_sdf_u2022", "arialuni_sdf_u2022", null),
        "老版 TMP 兼容（u2019）" => ("arialuni_sdf_u2019", "arialuni_sdf_u2019", null),
        "不注入中文字体" => (null, null, null),
        _ => (GetFontFile(unityMajor), GetFontFile(unityMajor), null)
    };

    internal static string GetGameId(string gamePath)
    {
        var normalizedPath = Path.GetFullPath(gamePath).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)))[..12];
        // 兼容层：XUnity 的 ExIni 配置读取会把 "%...%" 误认为环境变量占位符，遇到含 "%"
        // 的值（如 URL 编码空格的 %20）会陷入无限循环，导致游戏主线程卡死、窗口不出现。
        // 因此把文件名统一为 URL 安全字符（仅保留字母数字 ._-），从源头杜绝 % 的产生，
        // 保证生成的 Url、缓存路径、桥接端 gameId 三处完全一致。
        var rawName = Path.GetFileNameWithoutExtension(gamePath);
        var safeName = new StringBuilder(rawName.Length);
        foreach (var c in rawName)
        {
            safeName.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_');
        }
        return $"{safeName}-{hash}";
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

    private void LoadTmpFontModes()
    {
        try
        {
            if (!File.Exists(_fontSettingsFile)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_fontSettingsFile));
            if (loaded is null) return;
            foreach (var (key, value) in loaded)
            {
                var mode = value == "TMP 主字体覆盖（u2022）" ? "TMP 回退字体（u2022）" : value;
                if (TmpFontModes.Contains(mode)) _fontModes[key] = mode;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            RuntimeLog.Write($"[字体] 读取字体模式失败：{exception.Message}");
        }
    }

    private void SaveTmpFontModes()
    {
        try
        {
            File.WriteAllText(_fontSettingsFile, JsonSerializer.Serialize(_fontModes));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.Write($"[字体] 保存字体模式失败：{exception.Message}");
        }
    }

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
