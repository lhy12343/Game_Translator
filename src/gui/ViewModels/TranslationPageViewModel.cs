using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameTranslator.Gui.Services;

namespace GameTranslator.Gui.ViewModels;

public partial class TranslationPageViewModel : ViewModelBase
{
    private readonly TranslationRuntime _runtime;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TranslateCommand))]
    private string _sourceText = "";

    [ObservableProperty]
    private string _translatedText = "";

    [ObservableProperty]
    private string _status = "请先在“翻译配置”中填写并保存 API 配置。";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TranslateCommand))]
    private bool _isTranslating;

    public TranslationPageViewModel(TranslationRuntime runtime)
    {
        _runtime = runtime;
        if (!string.IsNullOrWhiteSpace(runtime.CurrentConfig.BaseUrl)) Status = "可以开始翻译。";
    }

    private bool CanTranslate() => !IsTranslating && !string.IsNullOrWhiteSpace(SourceText);

    [RelayCommand(CanExecute = nameof(CanTranslate))]
    private async Task TranslateAsync(CancellationToken cancellationToken)
    {
        IsTranslating = true;
        Status = "翻译中...";
        try
        {
            var result = await _runtime.TranslateAsync(SourceText, cancellationToken);
            TranslatedText = result.Text;
            Status = $"完成 · {result.ElapsedMilliseconds} ms · {CacheSourceText(result.Source)}";
        }
        catch (OperationCanceledException)
        {
            Status = "翻译已取消。";
        }
        catch (Exception exception)
        {
            Status = $"翻译失败：{exception.Message}";
        }
        finally
        {
            IsTranslating = false;
        }
    }

    [RelayCommand]
    private void CancelTranslation() => TranslateCommand.Cancel();

    private static string CacheSourceText(TranslationSource source) => source switch
    {
        TranslationSource.MemoryCache => "内存缓存",
        TranslationSource.PersistentCache => "持久缓存",
        _ => "API"
    };
}
