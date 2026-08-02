using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameTranslator.Gui.Services;

namespace GameTranslator.Gui.ViewModels;

public partial class GlossaryPageViewModel : ViewModelBase
{
    private readonly TranslationRuntime _runtime;

    public ObservableCollection<GlossaryEntry> Entries { get; } = [];
    public string[] Categories { get; } = ["通用", "角色名", "地名", "物品", "技能"];

    [ObservableProperty]
    private string _newSource = "";

    [ObservableProperty]
    private string _newTarget = "";

    [ObservableProperty]
    private string _newCategory = "通用";

    [ObservableProperty]
    private string _status = "";

    public GlossaryPageViewModel(TranslationRuntime runtime)
    {
        _runtime = runtime;
        foreach (var entry in runtime.LoadGlossary()) Entries.Add(entry);
    }

    [RelayCommand]
    private void AddEntry()
    {
        try
        {
            Entries.Add(_runtime.AddGlossary(NewSource, NewTarget, NewCategory));
            NewSource = "";
            NewTarget = "";
            Status = "术语已保存";
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
    }

    [RelayCommand]
    private void DeleteEntry(GlossaryEntry? entry)
    {
        if (entry is null) return;
        try
        {
            _runtime.DeleteGlossary(entry.Id);
            Entries.Remove(entry);
            Status = "术语已删除";
        }
        catch (Exception exception)
        {
            Status = $"删除失败：{exception.Message}";
        }
    }
}
