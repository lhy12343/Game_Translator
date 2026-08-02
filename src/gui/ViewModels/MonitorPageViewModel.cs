using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using GameTranslator.Gui.Services;

namespace GameTranslator.Gui.ViewModels;

public partial class MonitorPageViewModel : ViewModelBase
{
    private readonly HomePageViewModel _home;
    private readonly TranslationRuntime _runtime;
    private readonly DispatcherTimer _timer;
    private Process? _process;
    private int? _processId;
    private TimeSpan _lastProcessorTime;
    private long _lastSampleTimestamp;

    [ObservableProperty]
    private string _translationLatency = "—";

    [ObservableProperty]
    private string _cacheHitRate = "0.0%";

    [ObservableProperty]
    private string _queueLength = "0";

    [ObservableProperty]
    private string _cpuUsage = "—";

    [ObservableProperty]
    private string _memoryUsage = "—";

    [ObservableProperty]
    private string _totalTranslated = "0";

    public MonitorPageViewModel(HomePageViewModel home, TranslationRuntime runtime)
    {
        _home = home;
        _runtime = runtime;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) =>
        {
            SampleProcess();
            SampleTranslation();
        };
        _timer.Start();
    }

    private void SampleTranslation()
    {
        var metrics = _runtime.GetMetrics();
        TranslationLatency = metrics.LastLatencyMilliseconds < 0 ? "—" : $"{metrics.LastLatencyMilliseconds} ms";
        CacheHitRate = metrics.RequestCount == 0 ? "0.0%" : $"{metrics.CacheHitCount * 100d / metrics.RequestCount:F1}%";
        QueueLength = metrics.QueueLength.ToString();
        TotalTranslated = metrics.CompletedCount.ToString();
    }

    internal static double CalculateCpuUsage(TimeSpan processorDelta, TimeSpan elapsed, int processorCount)
    {
        if (elapsed <= TimeSpan.Zero || processorCount <= 0) return 0;
        return Math.Clamp(processorDelta.TotalMilliseconds / elapsed.TotalMilliseconds / processorCount * 100, 0, 100);
    }

    private void SampleProcess()
    {
        var processId = _home.ActiveProcessId;
        if (processId is null)
        {
            ResetProcessMetrics();
            return;
        }

        try
        {
            if (_processId != processId)
            {
                _process?.Dispose();
                _process = Process.GetProcessById(processId.Value);
                _processId = processId;
                _lastProcessorTime = _process.TotalProcessorTime;
                _lastSampleTimestamp = Stopwatch.GetTimestamp();
                _process.Refresh();
                MemoryUsage = $"{_process.WorkingSet64 / 1024d / 1024d:F1} MB";
                CpuUsage = "采样中";
                return;
            }

            _process!.Refresh();
            if (_process.HasExited)
            {
                ResetProcessMetrics();
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var processorTime = _process.TotalProcessorTime;
            CpuUsage = $"{CalculateCpuUsage(processorTime - _lastProcessorTime, Stopwatch.GetElapsedTime(_lastSampleTimestamp, now), Environment.ProcessorCount):F1}%";
            MemoryUsage = $"{_process.WorkingSet64 / 1024d / 1024d:F1} MB";
            _lastProcessorTime = processorTime;
            _lastSampleTimestamp = now;
        }
        catch (ArgumentException)
        {
            ResetProcessMetrics();
        }
        catch (InvalidOperationException)
        {
            ResetProcessMetrics();
        }
        catch (Win32Exception)
        {
            ResetProcessMetrics();
        }
    }

    private void ResetProcessMetrics()
    {
        _process?.Dispose();
        _process = null;
        _processId = null;
        CpuUsage = "—";
        MemoryUsage = "—";
    }
}
