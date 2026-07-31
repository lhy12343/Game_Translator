using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GameTranslator.Gui.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private ViewModelBase _currentPage;

    [ObservableProperty]
    private NavItem? _selectedNav;

    public ObservableCollection<NavItem> NavItems { get; } = new()
    {
        new("首页", "🏠", typeof(HomePageViewModel)),
        new("翻译配置", "⚙", typeof(ApiConfigPageViewModel)),
        new("翻译设置", "🔧", typeof(TranslationPageViewModel)),
        new("性能监控", "📊", typeof(MonitorPageViewModel)),
        new("术语管理", "📖", typeof(GlossaryPageViewModel)),
    };

    public MainViewModel()
    {
        _currentPage = new HomePageViewModel();
        _selectedNav = NavItems[0];
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value == null) return;
        CurrentPage = value.CreatePage();
    }
}

public partial class NavItem : ObservableObject
{
    public string Label { get; }
    public string Icon { get; }
    public Type PageType { get; }

    public NavItem(string label, string icon, Type pageType)
    {
        Label = label;
        Icon = icon;
        PageType = pageType;
    }

    public ViewModelBase CreatePage()
    {
        return (ViewModelBase)Activator.CreateInstance(PageType)!;
    }
}
