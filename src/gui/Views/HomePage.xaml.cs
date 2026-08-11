using System;
using System.Windows;
using System.Windows.Controls;
using GameTranslator.Gui.ViewModels;
using Microsoft.Win32;

namespace GameTranslator.Gui.Views;

public partial class HomePage : UserControl
{
    public HomePage()
    {
        InitializeComponent();
    }

    private void OnBrowseGame(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Windows 程序 (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog() == true && DataContext is HomePageViewModel viewModel)
            viewModel.SelectGame(dialog.FileName);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedExe(e.Data) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var path = GetDroppedExe(e.Data);
        if (path is not null && DataContext is HomePageViewModel viewModel) viewModel.SelectGame(path);
    }

    private static string? GetDroppedExe(IDataObject data) =>
        data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files
        && string.Equals(System.IO.Path.GetExtension(files[0]), ".exe", StringComparison.OrdinalIgnoreCase)
            ? files[0]
            : null;
}
