using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameTranslator.Gui.Services;

namespace GameTranslator.Gui.ViewModels;

public partial class ApiConfigPageViewModel : ViewModelBase
{
    private readonly TranslationRuntime _runtime;

    [ObservableProperty]
    private string _baseUrl;

    [ObservableProperty]
    private string _apiKey = "";

    [ObservableProperty]
    private string _modelName;

    [ObservableProperty]
    private string _sourceLanguage;

    [ObservableProperty]
    private string _targetLanguage;

    [ObservableProperty]
    private int _timeoutSeconds;

    [ObservableProperty]
    private string _connectionStatus;

    public IReadOnlyList<string> SourceLanguages => TranslatorConfig.SupportedSourceLanguages;
    public XUnityBridgeServer GameBridge => _runtime.Bridge;

    public ApiConfigPageViewModel(TranslationRuntime runtime)
    {
        _runtime = runtime;
        var config = runtime.CurrentConfig;
        _baseUrl = config.BaseUrl;
        _modelName = config.Model;
        _sourceLanguage = config.SourceLanguage == TranslatorConfig.Japanese
            ? TranslatorConfig.Japanese
            : TranslatorConfig.English;
        _targetLanguage = TranslatorConfig.Chinese;
        _timeoutSeconds = config.TimeoutSeconds;
        _connectionStatus = runtime.ConfigurationLoadError
                            ?? (string.IsNullOrEmpty(config.BaseUrl) ? "等待配置" : "已加载本地配置");
    }

    [RelayCommand]
    private async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        ConnectionStatus = "正在认证...";
        try
        {
            await _runtime.TestConnectionAsync(CreateConfig(), cancellationToken);
            ConnectionStatus = "认证成功";
        }
        catch (OperationCanceledException)
        {
            ConnectionStatus = "认证已取消";
        }
        catch (Exception exception)
        {
            ConnectionStatus = $"认证失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private void SaveConfig()
    {
        try
        {
            _runtime.SaveConfig(CreateConfig());
            ApiKey = "";
            ConnectionStatus = "配置已安全保存";
        }
        catch (Exception exception)
        {
            ConnectionStatus = $"保存失败：{exception.Message}";
        }
    }

    private TranslatorConfig CreateConfig() => new(
        BaseUrl,
        ApiKey,
        ModelName,
        SourceLanguage,
        TargetLanguage,
        TimeoutSeconds);
}
