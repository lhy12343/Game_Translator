using System.Windows.Controls;
using GameTranslator.Gui.ViewModels;

namespace GameTranslator.Gui.Views;

public partial class ApiConfigPage : UserControl
{
    public ApiConfigPage()
    {
        InitializeComponent();
    }

    private void OnApiKeyChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ApiConfigPageViewModel viewModel && sender is PasswordBox passwordBox)
            viewModel.ApiKey = passwordBox.Password;
    }
}
