using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GameTranslator.Gui.ViewModels;

public partial class GlossaryPageViewModel : ViewModelBase
{
    public ObservableCollection<GlossaryEntry> Entries { get; } = new()
    {
        new("サクラ", "樱花", "角色名"),
        new("タケル", "武", "角色名"),
        new("東京", "东京", "地名"),
        new("魔法", "魔法", "通用"),
    };

    [ObservableProperty]
    private string _newSource = "";

    [ObservableProperty]
    private string _newTarget = "";

    [ObservableProperty]
    private string _newCategory = "通用";

    [RelayCommand]
    private void AddEntry()
    {
        if (string.IsNullOrWhiteSpace(NewSource) || string.IsNullOrWhiteSpace(NewTarget))
            return;
        Entries.Add(new GlossaryEntry(NewSource, NewTarget, NewCategory));
        NewSource = "";
        NewTarget = "";
    }

    [RelayCommand]
    private void DeleteEntry(GlossaryEntry entry)
    {
        Entries.Remove(entry);
    }
}

public class GlossaryEntry
{
    public string Source { get; }
    public string Target { get; }
    public string Category { get; }

    public GlossaryEntry(string source, string target, string category)
    {
        Source = source;
        Target = target;
        Category = category;
    }
}
