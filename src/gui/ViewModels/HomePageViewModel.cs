using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GameTranslator.Gui.ViewModels;

public partial class HomePageViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _selectedProcess = "未选择进程";

    [ObservableProperty]
    private string _selectedProcessId = "-";

    [ObservableProperty]
    private string _selectedWindowTitle = "-";

    [ObservableProperty]
    private string _monitorStatus = "空闲";

    [ObservableProperty]
    private ProcessItem? _selectedProcessItem;

    public int? ActiveProcessId { get; private set; }

    public ObservableCollection<ProcessItem> ProcessList { get; } = [];

    public HomePageViewModel()
    {
        RefreshProcesses();
    }

    [RelayCommand]
    private void RefreshProcesses()
    {
        var selectedId = SelectedProcessItem?.Id;
        var items = new List<ProcessItem>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == Environment.ProcessId) continue;
                if (process.MainWindowHandle == IntPtr.Zero) continue;
                items.Add(new ProcessItem(process.Id, $"{process.ProcessName}.exe", process.MainWindowTitle));
            }
            catch (InvalidOperationException)
            {
                // 进程在枚举期间退出，跳过即可。
            }
            catch (Win32Exception)
            {
                // 系统进程可能拒绝读取，跳过即可。
            }
            finally
            {
                process.Dispose();
            }
        }

        ProcessList.Clear();
        foreach (var item in items
                     .OrderBy(item => item.ExecutableName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Id))
        {
            ProcessList.Add(item);
        }

        SelectedProcessItem = ProcessList.FirstOrDefault(item => item.Id == selectedId);
    }

    [RelayCommand]
    private void StartMonitoring()
    {
        if (SelectedProcessItem is null) return;

        ActiveProcessId = SelectedProcessItem.Id;
        SelectedProcess = SelectedProcessItem.ExecutableName;
        SelectedProcessId = SelectedProcessItem.Id.ToString();
        SelectedWindowTitle = string.IsNullOrWhiteSpace(SelectedProcessItem.WindowTitle)
            ? "无窗口标题"
            : SelectedProcessItem.WindowTitle;
        MonitorStatus = "监控中";
    }

    [RelayCommand]
    private void StopMonitoring()
    {
        ActiveProcessId = null;
        MonitorStatus = "空闲";
        SelectedProcess = "未选择进程";
        SelectedProcessId = "-";
        SelectedWindowTitle = "-";
    }
}

public sealed record ProcessItem(int Id, string ExecutableName, string WindowTitle)
{
    public override string ToString() => string.IsNullOrWhiteSpace(WindowTitle)
        ? $"{ExecutableName}  (PID: {Id})"
        : $"{ExecutableName}  (PID: {Id})  - {WindowTitle}";
}
