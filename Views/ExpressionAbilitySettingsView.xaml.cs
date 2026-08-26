using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Huaxiazi.ViewModels;

namespace Huaxiazi.Views;

public partial class ExpressionAbilitySettingsView : UserControl
{
    public ExpressionAbilitySettingsView() => InitializeComponent();

    private async void ImportSkill_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel) return;
        var dialog = new OpenFileDialog { Filter = "Agent Skill|SKILL.md;*.zip|Skill 文件|SKILL.md|ZIP 包|*.zip", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            await viewModel.InspectAgentSkillAsync(dialog.FileName);
    }

    private void ExportSkill_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel { SelectedAgentSkill: not null } viewModel) return;
        var dialog = new SaveFileDialog { Filter = "ZIP 包|*.zip", FileName = viewModel.SelectedAgentSkill.Name + ".zip", AddExtension = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) viewModel.ExportSelectedAgentSkill(dialog.FileName);
    }
}
