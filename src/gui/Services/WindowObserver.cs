using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace GameTranslator.Gui.Services;

/// <summary>通过 Win32 API 枚举指定进程的顶层窗口，用于监控游戏窗口是否出现。</summary>
internal static class WindowObserver
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    /// <summary>返回进程拥有的顶层窗口列表（标题, 是否可见）。</summary>
    public static List<(string Title, bool Visible)> FindWindows(int processId)
    {
        var result = new List<(string, bool)>();
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == (uint)processId)
            {
                var sb = new StringBuilder(512);
                GetWindowText(hWnd, sb, sb.Capacity);
                result.Add((sb.ToString(), IsWindowVisible(hWnd)));
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
