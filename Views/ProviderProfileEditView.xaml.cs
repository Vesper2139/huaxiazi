using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Huaxiazi.Models;
using Huaxiazi.ViewModels;

namespace Huaxiazi.Views;

public partial class ProviderProfileEditView : UserControl
{
    private bool _synchronizingApiKeyEditors;
    private SettingsViewModel? _subscribedViewModel;

    public ProviderProfileEditView()
    {
        InitializeComponent();
        Loaded += (_, _) => SynchronizeApiKeyEditors();
        DataContextChanged += (_, _) => SubscribeToViewModel();
        Loaded += (_, _) => SubscribeToViewModel();
        Unloaded += (_, _) => UnsubscribeFromViewModel();
    }

    private SettingsViewModel ViewModel => (SettingsViewModel)DataContext;

    private void SubscribeToViewModel()
    {
        if (ReferenceEquals(_subscribedViewModel, DataContext)) return;
        UnsubscribeFromViewModel();
        if (DataContext is not SettingsViewModel viewModel) return;
        _subscribedViewModel = viewModel;
        _subscribedViewModel.PropertyChanged += ViewModel_OnPropertyChanged;
    }

    private void UnsubscribeFromViewModel()
    {
        if (_subscribedViewModel is not null)
            _subscribedViewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
        _subscribedViewModel = null;
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.SelectedProviderProfile) or nameof(SettingsViewModel.ApiKey))
            SynchronizeApiKeyEditors();

        if (e.PropertyName is nameof(SettingsViewModel.SelectedProviderProfile) or nameof(SettingsViewModel.ModelId))
            SynchronizeModelSelector();
    }

    private void SynchronizeApiKeyEditors()
    {
        _synchronizingApiKeyEditors = true;
        try
        {
            // A saved key is loaded into the editor control (masked by default),
            // but is intentionally not copied into SettingsViewModel.ApiKey.
            var editorValue = string.IsNullOrEmpty(ViewModel.ApiKey)
                ? ViewModel.GetApiKeyForEditor()
                : ViewModel.ApiKey;
            ApiKeyBox.Password = editorValue;
            ApiKeyTextBox.Text = editorValue;
        }
        finally
        {
            _synchronizingApiKeyEditors = false;
        }
    }

    private void ApiKeyBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_synchronizingApiKeyEditors) ViewModel.ApiKey = ApiKeyBox.Password;
    }

    private void ApiKeyTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_synchronizingApiKeyEditors) ViewModel.ApiKey = ApiKeyTextBox.Text;
    }

    private void ProviderNameTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        // Ignore the initial binding pass; subsequent edits refresh the list
        // immediately even though ProviderProfile is intentionally a POCO.
        if (IsLoaded && DataContext is SettingsViewModel viewModel)
            viewModel.NotifyProviderProfileEdited();
    }

    private void ToggleApiKeyVisibilityButton_OnClick(object sender, RoutedEventArgs e)
    {
        var showPlainText = ApiKeyTextBox.Visibility != Visibility.Visible;
        if (showPlainText)
        {
            ApiKeyTextBox.Text = ApiKeyBox.Password;
            ApiKeyTextBox.Visibility = Visibility.Visible;
            ApiKeyBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            ApiKeyBox.Password = ApiKeyTextBox.Text;
            ApiKeyTextBox.Visibility = Visibility.Collapsed;
            ApiKeyBox.Visibility = Visibility.Visible;
        }
    }

    private void ClearApiKeyButton_OnClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearApiKeyCommand.Execute(null);
        SynchronizeApiKeyEditors();
    }

    private void OpenApiKeyUrlButton_OnClick(object sender, RoutedEventArgs e)
    {
        OpenExternalUrl(ViewModel.SelectedProviderApiKeyUrl);
    }

    private void AdvancedSettings_OnExpanded(object sender, RoutedEventArgs e)
    {
        // Keep the single page scroller as the only scroll surface and reveal
        // the custom controls without opening a second window or nested scroller.
        if (sender is FrameworkElement element)
            Dispatcher.BeginInvoke(new Action(element.BringIntoView), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void OpenExternalUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 外部浏览器启动失败时安静忽略，不影响设置操作
        }
    }

    private void ModelSelector_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: ModelDefinition model } selector)
        {
            ViewModel.ModelId = model.ModelId;
            selector.Text = model.ModelId;
        }
    }

    private void ModelSelector_OnLoaded(object sender, RoutedEventArgs e)
    {
        SynchronizeModelSelector(sender as ComboBox);
    }

    private void ModelSelector_OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox selector) CommitModelText(selector.Text);
    }

    internal void CommitModelText(string text) => ViewModel.ModelId = text.Trim();

    private void SynchronizeModelSelector(ComboBox? selector = null)
    {
        selector ??= ModelSelector;
        if (selector is null || ViewModel is null) return;

        var modelId = ViewModel.ModelId;
        if (string.Equals(selector.Text, modelId, StringComparison.Ordinal)) return;

        // Keep the OneWay binding as the source of truth first. ComboBox can
        // re-apply SelectedItem after a profile switch and overwrite Text;
        // the explicit fallback closes that WPF timing gap without changing
        // the binding mode or making the editable field write back eagerly.
        selector.GetBindingExpression(ComboBox.TextProperty)?.UpdateTarget();
        if (!string.Equals(selector.Text, modelId, StringComparison.Ordinal))
            selector.SetCurrentValue(ComboBox.TextProperty, modelId);
    }

}
