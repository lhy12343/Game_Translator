using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using GameTranslator.Gui.Services;

namespace GameTranslator.Gui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(args.ExceptionObject as Exception);
        try
        {
            AppPaths.Ensure(migrateLegacy: true);
            RuntimeLog.Initialize(AppPaths.DataDirectory);
        }
        catch (Exception exception)
        {
            ShowCrash(exception);
            Shutdown(-1);
            return;
        }
        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        ShowCrash(args.Exception);
        args.Handled = true;
    }

    private static void ShowCrash(Exception exception)
    {
        LogCrash(exception);
        MessageBox.Show(
            $"程序发生错误：\n{exception.Message}\n\n日志目录：{AppPaths.DataDirectory}",
            "GameTranslator 错误",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(Path.Combine(AppPaths.DataDirectory, "crash.log"), ex?.ToString() ?? "Unknown crash");
        }
        catch { }
    }
}
