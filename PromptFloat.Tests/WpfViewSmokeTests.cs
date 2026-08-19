using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using PromptFloat.Views;
using PromptFloat.ViewModels;
using PromptFloat.Services;
using Xunit;

namespace PromptFloat.Tests;

public sealed class WpfViewSmokeTests
{
    [Fact]
    public void MainWindow_DefaultLayoutIsCompactAndHasNoDataRouteNotice()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                App.ReplaceSettings(new Models.AppSettings { IncognitoMode = true });
                var window = new MainWindow();

                Assert.Equal(210, window.Height);
                Assert.Equal(152, window.MinHeight);
                Assert.DoesNotContain(
                    FindVisualChildren<TextBlock>(window),
                    text => BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path.Path == "DataRouteLabel");
                window.Close();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Theory]
    [InlineData(0.8, 448, 168)]
    [InlineData(1.5, 840, 315)]
    public void MainWindow_UiScaleResizesTheViewportWithItsContent(double scale, double expectedWidth, double expectedHeight)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                var settings = new Models.AppSettings
                {
                    IncognitoMode = true,
                    UiScale = scale,
                    MainWindowWidth = 560,
                    MainWindowHeight = 210
                };
                App.ReplaceSettings(settings);
                var window = new MainWindow();

                window.ApplyDisplayPreferences(settings);

                Assert.Equal(expectedWidth, window.Width, 3);
                Assert.Equal(expectedHeight, window.Height, 3);
                window.Show();
                window.UpdateLayout();
                var root = Assert.IsType<Grid>(window.FindName("RootGrid"));
                Assert.True(root.ActualWidth * scale <= window.ActualWidth + 1,
                    $"Scaled root width {root.ActualWidth * scale} exceeds {window.ActualWidth}");
                Assert.True(root.ActualHeight * scale <= window.ActualHeight + 1,
                    $"Scaled root height {root.ActualHeight * scale} exceeds {window.ActualHeight}");
                window.Close();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void MainEditor_WatermarkSharesTextMetricsAndHidesOnFocusOrInput()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                App.ReplaceSettings(new Models.AppSettings { IncognitoMode = true });
                var window = new MainWindow();
                var editor = Assert.IsType<TextBox>(window.FindName("MainTextBox"));
                var viewModel = Assert.IsType<MainViewModel>(window.DataContext);
                viewModel.UserInput = string.Empty;
                viewModel.OptimizedResult = string.Empty;
                window.ContentRendered += (_, _) =>
                {
                    editor.ApplyTemplate();
                    var watermark = Assert.IsType<TextBlock>(editor.Template.FindName("Watermark", editor));
                    var contentHost = Assert.IsType<ScrollViewer>(editor.Template.FindName("PART_ContentHost", editor));

                    Assert.Equal(contentHost.Margin, watermark.Margin);
                    Assert.Equal(editor.FontFamily, watermark.FontFamily);
                    Assert.Equal(editor.FontSize, watermark.FontSize);
                    Assert.Equal(Visibility.Visible, watermark.Visibility);

                    editor.Focus();
                    window.UpdateLayout();
                    Assert.True(editor.IsKeyboardFocusWithin);
                    Assert.Equal(Visibility.Collapsed, watermark.Visibility);

                    editor.Text = "shm";
                    window.UpdateLayout();
                    Assert.Equal(Visibility.Collapsed, watermark.Visibility);
                    window.Close();
                };
                window.ShowDialog();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void MainEditor_WatermarkTextTracksTheSelectedWorkflow()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                App.ReplaceSettings(new Models.AppSettings { IncognitoMode = true });
                var window = new MainWindow();
                var editor = Assert.IsType<TextBox>(window.FindName("MainTextBox"));
                var viewModel = Assert.IsType<MainViewModel>(window.DataContext);
                var tagBinding = Assert.IsType<Binding>(BindingOperations.GetBindingBase(editor, FrameworkElement.TagProperty));

                Assert.Equal(nameof(MainViewModel.EditorPlaceholderText), tagBinding.Path.Path);
                var initialPrefix = viewModel.IsPolishMode ? "输入想润色的原文…" : "输入你的想法或提示词…";
                Assert.StartsWith(initialPrefix, viewModel.EditorPlaceholderText);
                Assert.Contains("【上下文】", viewModel.EditorPlaceholderText);
                viewModel.SelectModeCommand.Execute(Models.ApplicationMode.PromptOptimize);
                Assert.StartsWith("输入你的想法或提示词…", viewModel.EditorPlaceholderText);
                window.Close();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void SettingsView_VisibleButtonsStayCompact()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                var view = new SettingsView();
                var secondary = Assert.IsType<Style>(view.Resources["SettingsActionButton"]);
                var primary = Assert.IsType<Style>(view.Resources["SettingsPrimaryButton"]);

                Assert.Contains(secondary.Setters, item => item is Setter setter
                    && setter.Property == FrameworkElement.MinHeightProperty && setter.Value is double value && value == 28);
                Assert.Contains(primary.Setters, item => item is Setter setter
                    && setter.Property == FrameworkElement.MinHeightProperty && setter.Value is double value && value == 28);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void SettingsView_HealthRows_CanMaterializeWithoutMissingThemeResources()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                var view = new SettingsView();
                var vm = Assert.IsType<SettingsViewModel>(view.DataContext);
                vm.HealthItems.Add(new HealthCheckItem("Config", HealthCheckScope.Local, HealthCheckStatus.Healthy, "配置正常"));
                var window = new Window { Content = view, Width = 900, Height = 700 };
                window.Show();
                window.UpdateLayout();
                window.Close();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void SettingsView_ConstructsWithRealResourcesAndBindings()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                _ = new SettingsView();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    [Theory]
    [InlineData(typeof(MainWindow))]
    [InlineData(typeof(FloatingBallWindow))]
    public void ShellWindow_ConstructsWithRealResources(Type windowType)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                if (Activator.CreateInstance(windowType) is Window window) window.Close();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void MainWindow_ExposesLiveModelSwitchAndARealStopAction()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                var window = new MainWindow();
                var providerSelector = Assert.IsType<ComboBox>(window.FindName("ProviderSelector"));
                var stopButton = Assert.IsType<Button>(window.FindName("StopGenerationButton"));
                var vm = Assert.IsType<MainViewModel>(window.DataContext);

                Assert.Equal(nameof(MainViewModel.ProviderProfiles),
                    BindingOperations.GetBinding(providerSelector, ItemsControl.ItemsSourceProperty)?.Path.Path);
                Assert.Equal(nameof(MainViewModel.CancelCommand),
                    BindingOperations.GetBinding(stopButton, Button.CommandProperty)?.Path.Path);

                vm.IsBusy = true;
                window.UpdateLayout();
                Assert.Equal(Visibility.Visible, stopButton.Visibility);
                window.Close();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void SettingsView_ExposesEveryPersistedCorePreference()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                var view = new SettingsView();
                AssertBinding(view, "OutputStyleSelector", ComboBox.SelectedItemProperty, "OutputStyle");
                AssertBinding(view, "CustomStyleInstructionsTextBox", TextBox.TextProperty, "CustomStyleInstructions");
                AssertBinding(view, "DefaultPolishScenarioSelector", ComboBox.SelectedItemProperty, "DefaultPolishScenario");
                AssertBinding(view, "ClipboardAutoReadCheckBox", System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, "ClipboardAutoRead");
                AssertBinding(view, "StartWithWindowsCheckBox", System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, "StartWithWindows");
                AssertBinding(view, "DefaultCategorySelector", ComboBox.SelectedItemProperty, "DefaultCategory");
                AssertBinding(view, "DefaultDepthSelector", ComboBox.SelectedItemProperty, "DefaultDepth");
                AssertBinding(view, "PromptCategoryOptionsList", ItemsControl.ItemsSourceProperty, "PromptCategoryOptions");
                AssertBinding(view, "PolishEnabledCheckBox", System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, "PolishEnabled");
                AssertBinding(view, "PromptOptimizeEnabledCheckBox", System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, "PromptOptimizeEnabled");
                AssertBinding(view, "UpdateCheckUrlTextBox", TextBox.TextProperty, "UpdateCheckUrl");
                AssertBinding(view, "TemperatureTextBox", TextBox.TextProperty, "SelectedProviderProfile.Temperature");
                AssertBinding(view, "TopPTextBox", TextBox.TextProperty, "SelectedProviderProfile.TopP");
                AssertBinding(view, "MaxTokensTextBox", TextBox.TextProperty, "SelectedProviderProfile.MaxTokens");
                AssertBinding(view, "CustomSystemPromptTextBox", TextBox.TextProperty, "CustomSystemPrompt");
                AssertBinding(view, "PreserveMeaningCheckBox", System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, "PreserveMeaning");
                AssertBinding(view, "MinimalRewriteCheckBox", System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, "MinimalRewrite");
                AssertBinding(view, "ProfessionalToneCheckBox", System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, "ProfessionalTone");
                AssertBinding(view, "OptimizationPresetList", ItemsControl.ItemsSourceProperty, "OptimizationPresets");
                AssertBinding(view, "ArchiveModeFilter", ComboBox.SelectedItemProperty, "ArchiveModeFilter");
                AssertBinding(view, "ArchiveFromTextBox", TextBox.TextProperty, "ArchiveFromText");
                AssertBinding(view, "ArchiveToTextBox", TextBox.TextProperty, "ArchiveToText");
                AssertBinding(view, "ArchiveOriginalPreview", TextBox.TextProperty, "SelectedArchiveItem.OriginalText");
                AssertBinding(view, "ArchiveResultPreview", TextBox.TextProperty, "SelectedArchiveItem.FinalText");
                AssertBinding(view, "FavoriteArchiveButton", Button.CommandProperty, "ToggleFavoriteCommand");
                AssertBinding(view, "ArchiveItemButton", Button.CommandProperty, "ToggleArchivedCommand");
                AssertBinding(view, "ExportFormatSelector", ComboBox.SelectedItemProperty, "SelectedExportFormat");
                AssertBinding(view, "ExportAllButton", Button.CommandProperty, "ExportAllCommand");
                AssertBinding(view, "BackupDataButton", Button.CommandProperty, "BackupDataCommand");
                AssertBinding(view, "ClearCacheButton", Button.CommandProperty, "ClearCacheCommand");
                AssertBinding(view, "HistoryEnabledCheckBox", System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, "HistoryEnabled");
                AssertBinding(view, "IncognitoModeCheckBox", System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, "IncognitoMode");
                AssertBinding(view, "SaveOriginalTextCheckBox", System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, "SaveOriginalText");
                AssertBinding(view, "SaveOptimizedTextCheckBox", System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, "SaveOptimizedText");
                AssertBinding(view, "AutoCopyAfterOptimizeCheckBox", System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, "AutoCopyAfterOptimize");
                AssertBinding(view, "HistoryRetentionDaysTextBox", TextBox.TextProperty, "HistoryRetentionDays");
                AssertBinding(view, "AutosaveDelayTextBox", TextBox.TextProperty, "AutosaveDelayMilliseconds");
                AssertBinding(view, "EditorFontSizeSlider", System.Windows.Controls.Primitives.RangeBase.ValueProperty, "EditorFontSize");
                AssertBinding(view, "UiScaleSlider", System.Windows.Controls.Primitives.RangeBase.ValueProperty, "UiScale");
                AssertBinding(view, "EditorDefaultHeightSlider", System.Windows.Controls.Primitives.RangeBase.ValueProperty, "EditorDefaultHeight");
                AssertBinding(view, "WindowOpacitySlider", System.Windows.Controls.Primitives.RangeBase.ValueProperty, "WindowOpacity");
                AssertBinding(view, "AnimationsEnabledCheckBox", System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, "AnimationsEnabled");
                AssertBinding(view, "FloatingBallOpacitySlider", System.Windows.Controls.Primitives.RangeBase.ValueProperty, "FloatingBallOpacity");
                AssertBinding(view, "CloseBehaviorSelector", System.Windows.Controls.Primitives.Selector.SelectedValueProperty, "CloseBehavior");
                AssertBinding(view, "QuickPolishHotkeyTextBox", TextBox.TextProperty, "QuickPolishHotkey");
                AssertBinding(view, "QuickPromptHotkeyTextBox", TextBox.TextProperty, "QuickPromptHotkey");
                AssertBinding(view, "CopyResultHotkeyTextBox", TextBox.TextProperty, "CopyResultHotkey");
                AssertBinding(view, "AutoCheckUpdatesCheckBox", System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, "AutoCheckUpdates");
                AssertBinding(view, "HealthSummaryText", TextBlock.TextProperty, "HealthSummary");
                AssertBinding(view, "HealthCheckList", ItemsControl.ItemsSourceProperty, "HealthItems");
                AssertBinding(view, "RunHealthCheckButton", Button.CommandProperty, "RunHealthCheckCommand");
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    private static void AssertBinding(FrameworkElement root, string name, DependencyProperty property, string expectedPath)
    {
        var element = Assert.IsAssignableFrom<DependencyObject>(root.FindName(name));
        var binding = Assert.IsType<Binding>(BindingOperations.GetBindingBase(element, property));
        Assert.Equal(expectedPath, binding.Path.Path);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private static void EnsureApplicationResources()
    {
        var application = Application.Current ?? new Application();
        var resources = application.Resources;
        resources.MergedDictionaries.Clear();
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Huaxiazi;component/Resources/Themes/Dark.xaml", UriKind.Relative) });
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Huaxiazi;component/Resources/Styles/GlobalStyles.xaml", UriKind.Relative) });
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Huaxiazi;component/Resources/Icons/AppIcons.xaml", UriKind.Relative) });
    }
}
