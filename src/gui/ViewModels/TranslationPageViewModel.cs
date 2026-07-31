using CommunityToolkit.Mvvm.ComponentModel;

namespace GameTranslator.Gui.ViewModels;

public partial class TranslationPageViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool _enableCache = true;

    [ObservableProperty]
    private int _cacheSize = 10000;

    [ObservableProperty]
    private bool _enableRichText = true;

    [ObservableProperty]
    private bool _enableAutoLayout = true;

    [ObservableProperty]
    private double _fontScaleMin = 0.7;

    [ObservableProperty]
    private bool _enableOcr = false;

    [ObservableProperty]
    private int _ocrFps = 15;

    [ObservableProperty]
    private bool _enableOverlay = false;

    [ObservableProperty]
    private bool _enableTermConsistency = true;
}
