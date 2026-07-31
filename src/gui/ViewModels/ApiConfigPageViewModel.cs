using CommunityToolkit.Mvvm.ComponentModel;

namespace GameTranslator.Gui.ViewModels;

public partial class ApiConfigPageViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _apiUrl = "https://api.openai.com/v1/chat/completions";

    [ObservableProperty]
    private string _apiKey = "";

    [ObservableProperty]
    private string _modelName = "gpt-4o-mini";

    [ObservableProperty]
    private string _sourceLanguage = "日语";

    [ObservableProperty]
    private string _targetLanguage = "简体中文";

    [ObservableProperty]
    private bool _useBatchTranslation = true;

    [ObservableProperty]
    private int _batchWindowMs = 100;

    [ObservableProperty]
    private int _timeoutMs = 5000;

    [ObservableProperty]
    private string _connectionStatus = "未测试";

    [ObservableProperty]
    private bool _isConnected;
}
