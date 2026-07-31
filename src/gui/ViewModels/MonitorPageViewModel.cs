using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GameTranslator.Gui.ViewModels;

public partial class MonitorPageViewModel : ViewModelBase
{
    [ObservableProperty]
    private double _fps;

    [ObservableProperty]
    private double _hookLatencyMs;

    [ObservableProperty]
    private double _translationLatencyMs;

    [ObservableProperty]
    private double _cacheHitRate;

    [ObservableProperty]
    private int _queueLength;

    [ObservableProperty]
    private double _cpuUsage;

    [ObservableProperty]
    private double _memoryMb;

    [ObservableProperty]
    private int _totalTranslated;

    [ObservableProperty]
    private int _totalCacheHits;

    public MonitorPageViewModel()
    {
        // 模拟数据
        Fps = 59.8;
        HookLatencyMs = 0.03;
        TranslationLatencyMs = 320;
        CacheHitRate = 72.5;
        QueueLength = 3;
        CpuUsage = 2.1;
        MemoryMb = 45.2;
        TotalTranslated = 1024;
        TotalCacheHits = 742;
    }
}
