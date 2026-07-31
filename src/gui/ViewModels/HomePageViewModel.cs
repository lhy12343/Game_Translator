using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GameTranslator.Gui.ViewModels;

public partial class HomePageViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _selectedProcess = "未选择进程";

    [ObservableProperty]
    private bool _isHooked;

    [ObservableProperty]
    private string _gameEngine = "-";

    [ObservableProperty]
    private string _translateStatus = "空闲";

    [ObservableProperty]
    private int _translatedCount;

    [ObservableProperty]
    private int _cacheHitCount;

    [ObservableProperty]
    private string? _selectedProcessItem;

    public ObservableCollection<string> ProcessList { get; } = new()
    {
        "game.exe  (PID: 12345)  - Unity",
        "visualnovel.exe  (PID: 67890)  - Renpy",
        "rpgmaker_game.exe  (PID: 11111)  - RPGMaker",
    };

    [RelayCommand]
    private void RefreshProcesses() { }

    [RelayCommand]
    private void Attach()
    {
        if (SelectedProcessItem == null) return;
        SelectedProcess = SelectedProcessItem;
        IsHooked = true;
        TranslateStatus = "已连接";
        GameEngine = "Unity";
    }

    [RelayCommand]
    private void Detach()
    {
        IsHooked = false;
        TranslateStatus = "空闲";
        GameEngine = "-";
        SelectedProcess = "未选择进程";
    }
}
