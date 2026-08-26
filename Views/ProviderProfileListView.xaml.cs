using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PromptFloat.Models;
using PromptFloat.ViewModels;

namespace PromptFloat.Views;

public partial class ProviderProfileListView : UserControl
{
    public ProviderProfileListView()
    {
        InitializeComponent();
    }

    private SettingsViewModel ViewModel => (SettingsViewModel)DataContext;

    private void ProfileListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProfileListBox.SelectedItem is ProviderProfile profile)
            ViewModel.NavigateToProviderEditCommand.Execute(profile);
    }

    private void ProfileListBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (ProfileListBox.SelectedItem is not ProviderProfile profile) return;

        if (e.Key == Key.Enter)
        {
            ViewModel.NavigateToProviderEditCommand.Execute(profile);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            e.Handled = true;
            if (!ViewModel.CanRemoveProvider) return;
            var result = MessageBox.Show($"确定要删除配置“{profile.Name}”吗？", "删除配置",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (result == MessageBoxResult.Yes)
                ViewModel.RemoveProviderCommand.Execute(null);
        }
    }

    private void DeleteSelectedProfile_OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProviderProfile is not { } profile || !ViewModel.CanRemoveProvider) return;
        var result = MessageBox.Show($"确定要删除配置“{profile.Name}”吗？", "删除配置",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (result == MessageBoxResult.Yes)
            ViewModel.RemoveProviderCommand.Execute(null);
    }

}
