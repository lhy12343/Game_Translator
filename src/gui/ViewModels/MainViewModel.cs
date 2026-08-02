using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GameTranslator.Gui.Services;

namespace GameTranslator.Gui.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private ViewModelBase _currentPage;

    [ObservableProperty]
    private NavItem? _selectedNav;

    public ObservableCollection<NavItem> NavItems { get; }

    public MainViewModel()
    {
        var runtime = new TranslationRuntime();
        var home = new HomePageViewModel();
        NavItems =
        [
            new("首页", "🏠", home),
            new("翻译配置", "⚙", new ApiConfigPageViewModel(runtime)),
            new("翻译测试", "🔧", new TranslationPageViewModel(runtime)),
            new("性能监控", "📊", new MonitorPageViewModel(home, runtime)),
            new("术语管理", "📖", new GlossaryPageViewModel(runtime)),
        ];
        _currentPage = home;
        _selectedNav = NavItems[0];
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value == null) return;
        CurrentPage = value.Page;
    }
}

public sealed record NavItem(string Label, string Icon, ViewModelBase Page);
