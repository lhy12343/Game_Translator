using System;
using System.IO;

namespace GameTranslator.Gui.Services;

internal static class RuntimeLog
{
    private static readonly object Gate = new();
    private static string? _logFile;

    /// <summary>初始化文件日志（Release/Debug 都写入），目录必须已存在。</summary>
    public static void Initialize(string logDirectory)
    {
        try
        {
            Directory.CreateDirectory(logDirectory);
            _logFile = Path.Combine(logDirectory, "launch.log");
            lock (Gate)
                File.WriteAllText(_logFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===== GameTranslator 启动日志 ====={Environment.NewLine}");
        }
        catch
        {
            _logFile = null;
        }
    }

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
#if DEBUG
        Console.WriteLine(line);
#endif
        if (_logFile is not null)
        {
            lock (Gate)
            {
                try { File.AppendAllText(_logFile, line + Environment.NewLine); }
                catch { }
            }
        }
    }
}
