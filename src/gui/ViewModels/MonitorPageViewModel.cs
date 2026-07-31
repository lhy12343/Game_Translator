using CommunityToolkit.Mvvm.ComponentModel;

namespace GameTranslator.Gui.ViewModels;

public partial class MonitorPageViewModel : ViewModelBase
{
    [ObservableProperty]
    private double _fps = 59.8;

    [ObservableProperty]
    private double _hookLatencyMs = 0.03;

    [ObservableProperty]
    private double _translationLatencyMs = 320;

    [ObservableProperty]
    private double _cacheHitRate = 72.5;

    [ObservableProperty]
    private int _queueLength = 3;

    [ObservableProperty]
    private double _cpuUsage = 2.1;

    [ObservableProperty]
    private double _memoryMb = 45.2;

    [ObservableProperty]
    private int _totalTranslated = 1024;

    [ObservableProperty]
    private int _totalCacheHits = 742;
}
