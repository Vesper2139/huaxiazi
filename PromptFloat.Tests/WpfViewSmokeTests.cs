using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Threading;
using PromptFloat.Models;
using PromptFloat.Views;
using PromptFloat.ViewModels;
using PromptFloat.Services;
using Xunit;

namespace PromptFloat.Tests;

public sealed class WpfViewSmokeTests
{
    [Fact]
    public void OutOfRangeNumericSetting_UsesTheDangerBorderImmediately()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                var box = new TextBox { Style = Assert.IsType<Style>(Application.Current.FindResource("GlassPlainTextBox")) };
                IntegerInputBehavior.SetMinimum(box, 1);
                IntegerInputBehavior.SetMaximum(box, 100);
                var window = new Window { Content = box };
                window.Show();
                box.ApplyTemplate();

                box.Text = "999";
                window.UpdateLayout();

                var border = Assert.IsType<Border>(box.Template.FindName("Bd", box));
                Assert.Same(Application.Current.FindResource("DangerBrush"), border.BorderBrush);
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

                Assert.Equal(176, window.Height);
                Assert.Equal(158, window.MinHeight);
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

                    Assert.Equal(new Thickness(12, 8, 12, 8), editor.Padding);
                    Assert.Equal(editor.Padding, contentHost.Margin);
                    Assert.Equal(editor.Padding, watermark.Margin);
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
    public void MainEditor_LargeFontExpandsItsMinimumHeightBeyondTheRenderedLineBox()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                var settings = new AppSettings
                {
                    IncognitoMode = true,
                    EditorFontSize = 24,
                    EditorDefaultHeight = 28
                };
                App.ReplaceSettings(settings);
                var window = new MainWindow();
                var editor = Assert.IsType<TextBox>(window.FindName("MainTextBox"));

                window.ApplyDisplayPreferences(settings);

                var requiredLineHeight = Math.Ceiling(editor.FontSize * editor.FontFamily.LineSpacing);
                var requiredControlHeight = requiredLineHeight + editor.Padding.Top + editor.Padding.Bottom + 2;
                Assert.True(editor.MinHeight >= requiredControlHeight,
                    $"MinHeight={editor.MinHeight}, required={requiredControlHeight}");
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
    public void MainEditor_WatermarkTextTracksTheSelectedWorkflow()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                App.ReplaceSettings(new Models.AppSettings { IncognitoMode = true, DefaultMode = Models.ApplicationMode.Polish });
                var window = new MainWindow();
                var editor = Assert.IsType<TextBox>(window.FindName("MainTextBox"));
                var viewModel = Assert.IsType<MainViewModel>(window.DataContext);
                var modeSwitcher = Assert.IsType<Grid>(window.FindName("ModeSwitcher"));
                var polishSegment = Assert.IsType<ToggleButton>(window.FindName("PolishSegment"));
                var promptSegment = Assert.IsType<ToggleButton>(window.FindName("PromptSegment"));
                window.Show();
                window.UpdateLayout();
                viewModel.CurrentMode = Models.ApplicationMode.Polish;
                window.UpdateLayout();
                window.Dispatcher.Invoke(DispatcherPriority.DataBind, new Action(() => { }));
                Assert.Equal(2, modeSwitcher.Children.OfType<ToggleButton>().Count());
                Assert.Equal(92, modeSwitcher.Width);
                Assert.Equal("润色", viewModel.ModeToggleLabel);
                var tagBinding = Assert.IsType<Binding>(BindingOperations.GetBindingBase(editor, FrameworkElement.TagProperty));

                Assert.Equal(nameof(MainViewModel.EditorPlaceholderText), tagBinding.Path.Path);
                var initialPrefix = viewModel.IsPolishMode ? "粘贴或输入要润色的内容…" : "描述你想让 AI 完成的任务…";
                Assert.StartsWith(initialPrefix, viewModel.EditorPlaceholderText);
                Assert.Contains("可选：补充对象、目的或语气", viewModel.EditorPlaceholderText);
                Assert.NotNull(promptSegment.Command);
                promptSegment.Command.Execute(promptSegment.CommandParameter);
                Assert.StartsWith("描述你想让 AI 完成的任务…", viewModel.EditorPlaceholderText);
                Assert.Contains("可选：补充背景、限制或输出格式", viewModel.EditorPlaceholderText);
                Assert.Equal("提示词", viewModel.ModeToggleLabel);
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
                    && setter.Property == FrameworkElement.MinHeightProperty && setter.Value is double value && value == 32);
                Assert.Contains(primary.Setters, item => item is Setter setter
                    && setter.Property == FrameworkElement.MinHeightProperty && setter.Value is double value && value == 32);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void SettingsView_ProviderCardsForwardMouseWheelToThePage()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                var providerCards = new ListBox { Height = 48 };
                providerCards.Items.Add("OpenAI");
                NestedScrollBehavior.SetForwardMouseWheelToParent(providerCards, true);
                var pageContent = new StackPanel();
                pageContent.Children.Add(providerCards);
                pageContent.Children.Add(new Border { Height = 600 });
                var pageScroller = new ScrollViewer { Content = pageContent };
                pageScroller.Measure(new Size(500, 200));
                pageScroller.Arrange(new Rect(0, 0, 500, 200));
                pageScroller.UpdateLayout();

                Assert.True(pageScroller.ScrollableHeight > 0,
                    $"ScrollableHeight={pageScroller.ScrollableHeight}, ViewportHeight={pageScroller.ViewportHeight}, ExtentHeight={pageScroller.ExtentHeight}, ActualHeight={pageScroller.ActualHeight}");
                Assert.Equal(0, pageScroller.VerticalOffset);

                var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent,
                    Source = providerCards
                };
                providerCards.RaiseEvent(wheel);
                pageScroller.UpdateLayout();

                Assert.True(wheel.Handled);
                Assert.True(pageScroller.VerticalOffset > 0);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void SettingsView_ApiFieldsAndProviderCardsStayCompactAtProductSize()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                App.ReplaceSettings(new AppSettings());
                var view = new SettingsView();

                var keyBox = Assert.IsType<PasswordBox>(FindProfileEditor(view).FindName("ApiKeyBox"));
                var modelId = Assert.IsType<ComboBox>(FindProfileEditor(view).FindName("ModelSelector"));
                var providerCardStyle = Assert.IsType<Style>(view.Resources["ProviderPresetItem"]);

                Assert.Equal(34, keyBox.Height);
                Assert.Equal(34, modelId.Height);
                Assert.Contains(providerCardStyle.Setters, item => item is Setter setter
                    && setter.Property == FrameworkElement.MinHeightProperty && setter.Value is double value && value == 42);
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

    [Fact]
    public void SettingsView_ProviderPicker_ListsAllVendorsAndSelectingAppliesPreset()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                App.ReplaceSettings(new AppSettings());
                var view = new SettingsView();
                var vm = Assert.IsType<SettingsViewModel>(view.DataContext);
                var combo = Assert.IsType<ComboBox>(FindProfileEditor(view).FindName("ProviderPlatformSelector"));

                // 供应商下拉绑定到带分组的产品视图（XAML 接线正确）
                var itemsBinding = BindingOperations.GetBinding(combo, ItemsControl.ItemsSourceProperty);
                Assert.NotNull(itemsBinding);
                Assert.Equal("ProviderPlatformView", itemsBinding.Path.Path);
                var selectedBinding = BindingOperations.GetBinding(combo, System.Windows.Controls.Primitives.Selector.SelectedItemProperty);
                Assert.Equal("SelectedProviderPlatform", selectedBinding?.Path.Path);

                // 22 家预设全量可得
                Assert.Equal(22, vm.ProviderPlatforms.Count);
                Assert.Contains(vm.ProviderPlatforms, p => p.DisplayName == "DeepSeek");
                Assert.Contains(vm.ProviderPlatforms, p => p.DisplayName == "Kimi（月之暗面）");

                // 点击选中 DeepSeek（等价于下拉选中）→ 自动填充 ApiBase/模型/协议
                vm.SelectedProviderPlatform = ProviderPlatformCatalog.Get(ProviderPlatform.DeepSeek);
                var profile = vm.SelectedProviderProfile!;
                Assert.Equal(ProviderPlatform.DeepSeek, profile.Platform);
                Assert.Equal("https://api.deepseek.com/v1", profile.ApiBase);
                Assert.Equal("deepseek-v4-flash", profile.Model);

                // 模型下拉绑定 AvailableModels 且联动到 DeepSeek 模型列表
                var modelCombo = Assert.IsType<ComboBox>(FindProfileEditor(view).FindName("ModelSelector"));
                var modelBinding = BindingOperations.GetBinding(modelCombo, ItemsControl.ItemsSourceProperty);
                Assert.Equal("AvailableModels", modelBinding?.Path.Path);
                Assert.Contains(vm.AvailableModels, m => m.ModelId == "deepseek-v4-flash");
                Assert.Contains(vm.AvailableModels, m => m.ModelId == "deepseek-v4-pro");
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    [Fact]
    public void SettingsView_ModelControlsReflectTheSelectedProfilesModelId()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                App.ReplaceSettings(new AppSettings());
                var view = new SettingsView();
                var viewModel = Assert.IsType<SettingsViewModel>(view.DataContext);
                viewModel.ModelId = "gpt-4o-mini";
                var window = new Window { Content = view, Width = 1120, Height = 810 };
                window.Show();
                window.UpdateLayout();

                var modelSelector = Assert.IsType<ComboBox>(FindProfileEditor(view).FindName("ModelSelector"));
                Assert.Equal("gpt-4o-mini", modelSelector.Text);

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
    public void SettingsView_ModelSelectorRefreshesWhenModelIdChangesAfterEditorLoaded()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                App.ReplaceSettings(new AppSettings());
                var view = new SettingsView();
                var viewModel = Assert.IsType<SettingsViewModel>(view.DataContext);
                var window = new Window { Content = view, Width = 1120, Height = 810 };
                window.Show();
                window.UpdateLayout();

                var modelSelector = Assert.IsType<ComboBox>(FindProfileEditor(view).FindName("ModelSelector"));
                viewModel.ModelId = "runtime-model-2026";
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);

                Assert.Equal("runtime-model-2026", modelSelector.Text);
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
    public void SettingsView_ModelSelectorAcceptsAnActualCustomModelId()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                App.ReplaceSettings(new AppSettings());
                var view = new SettingsView();
                var viewModel = Assert.IsType<SettingsViewModel>(view.DataContext);
                var editor = FindProfileEditor(view);
                var modelSelector = Assert.IsType<ComboBox>(editor.FindName("ModelSelector"));
                editor.CommitModelText("vendor-model-2026-08");

                Assert.True(modelSelector.IsEditable);
                Assert.Equal("vendor-model-2026-08", viewModel.ModelId);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void SettingsView_SelectingProviderPresetPrefillsAndKeepsItsRecommendedModel()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                App.ReplaceSettings(new AppSettings());
                var view = new SettingsView();
                var viewModel = Assert.IsType<SettingsViewModel>(view.DataContext);
                var window = new Window { Content = view, Width = 1120, Height = 810 };
                window.Show();
                viewModel.SelectedProviderPlatform = ProviderPlatformCatalog.Get(ProviderPlatform.DeepSeek);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                window.UpdateLayout();

                var modelSelector = Assert.IsType<ComboBox>(FindProfileEditor(view).FindName("ModelSelector"));
                Assert.Equal("deepseek-v4-flash", viewModel.ModelId);
                Assert.Equal("deepseek-v4-flash", modelSelector.Text);

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
    public void SettingsView_ProfileSwitch_PreservesOnlyPendingKeyDraftsInPasswordEditor()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                App.ReplaceSettings(new AppSettings());
                var view = new SettingsView();
                var vm = Assert.IsType<SettingsViewModel>(view.DataContext);
                var profileSelector = Assert.IsType<ComboBox>(FindProfileEditor(view).FindName("ProviderPlatformSelector"));
                var keyBox = Assert.IsType<PasswordBox>(FindProfileEditor(view).FindName("ApiKeyBox"));
                var first = vm.SelectedProviderProfile!;
                vm.ApiKey = "first-draft";
                vm.AddProviderCommand.Execute(null);
                vm.ApiKey = "second-draft";

                vm.SelectedProviderProfile = first;
                profileSelector.GetBindingExpression(System.Windows.Controls.Primitives.Selector.SelectedItemProperty)?.UpdateTarget();
                view.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

                Assert.Equal("first-draft", keyBox.Password);
                Assert.Equal("first-draft", vm.ApiKey);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void SettingsView_ClearKeyButton_ClearsThePasswordEditorAndStagesRemoval()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                EnsureApplicationResources();
                App.ReplaceSettings(new AppSettings());
                var view = new SettingsView();
                var window = new Window { Content = view, Width = 900, Height = 700 };
                window.Show();
                window.UpdateLayout();
                var vm = Assert.IsType<SettingsViewModel>(view.DataContext);
                var keyBox = Assert.IsType<PasswordBox>(FindProfileEditor(view).FindName("ApiKeyBox"));
                var clearButton = Assert.IsType<Button>(FindProfileEditor(view).FindName("ClearApiKeyButton"));
                vm.ApiKey = "draft-key";
                keyBox.Password = "draft-key";

                var peer = new ButtonAutomationPeer(clearButton);
                var invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
                invoke.Invoke();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                Assert.Equal(string.Empty, keyBox.Password);
                Assert.Equal(string.Empty, vm.ApiKey);
                Assert.Equal("保存后移除", vm.ApiKeyStateText);
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
                var ability = Assert.IsType<ExpressionAbilitySettingsView>(view.FindName("ExpressionAbilitySettingsPage"));
                AssertBinding(ability, "OutputStyleSelector", ComboBox.SelectedItemProperty, "OutputStyle");
                AssertBinding(ability, "CustomStyleInstructionsTextBox", TextBox.TextProperty, "CustomStyleInstructions");
                AssertBinding(ability, "DefaultPolishScenarioSelector", ComboBox.SelectedItemProperty, "DefaultPolishScenario");
                AssertBinding(view, "ClipboardAutoReadCheckBox", System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, "ClipboardAutoRead");
                AssertBinding(view, "StartWithWindowsCheckBox", System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, "StartWithWindows");
                AssertBinding(ability, "DefaultCategorySelector", ComboBox.SelectedItemProperty, "DefaultCategory");
                AssertBinding(ability, "DefaultDepthSelector", ComboBox.SelectedItemProperty, "DefaultDepth");
                AssertBinding(view, "PromptCategoryOptionsList", ItemsControl.ItemsSourceProperty, "PromptCategoryOptions");
                AssertBinding(view, "UpdateCheckUrlTextBox", TextBox.TextProperty, "UpdateCheckUrl");
                AssertBinding(FindProfileEditor(view), "ModelSelector", ComboBox.TextProperty, "ModelId");
                AssertBinding(ability, "CustomSystemPromptTextBox", TextBox.TextProperty, "CustomSystemPrompt");
                AssertBinding(ability, "PreserveMeaningCheckBox", System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, "PreserveMeaning");
                AssertBinding(ability, "MinimalRewriteCheckBox", System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, "MinimalRewrite");
                AssertBinding(ability, "ProfessionalToneCheckBox", System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, "ProfessionalTone");
                AssertBinding(ability, "OptimizationPresetList", ItemsControl.ItemsSourceProperty, "OptimizationPresets");
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
                AssertBinding(ability, "AutoCopyAfterOptimizeCheckBox", System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, "AutoCopyAfterOptimize");
                AssertBinding(view, "HistoryRetentionDaysTextBox", TextBox.TextProperty, "HistoryRetentionDays");
                AssertBinding(view, "AutosaveDelayTextBox", TextBox.TextProperty, "AutosaveDelayMilliseconds");
                AssertBinding(view, "EditorFontSizeSlider", System.Windows.Controls.Primitives.RangeBase.ValueProperty, "EditorFontSize");
                AssertBinding(view, "UiScaleSlider", System.Windows.Controls.Primitives.RangeBase.ValueProperty, "UiScale");
                AssertBinding(view, "EditorDefaultHeightSlider", System.Windows.Controls.Primitives.RangeBase.ValueProperty, "EditorDefaultHeight");
                AssertBinding(view, "WindowOpacitySlider", System.Windows.Controls.Primitives.RangeBase.ValueProperty, "WindowOpacity");
                AssertBinding(view, "AnimationsEnabledCheckBox", System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, "AnimationsEnabled");
                AssertBinding(view, "FloatingBallOpacitySlider", System.Windows.Controls.Primitives.RangeBase.ValueProperty, "FloatingBallOpacity");
                AssertBinding(view, "FloatingBallSizeSlider", System.Windows.Controls.Primitives.RangeBase.ValueProperty, "FloatingBallSize");
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

    private static ProviderProfileEditView FindProfileEditor(DependencyObject root) =>
        Assert.IsType<ProviderProfileEditView>(Assert.IsType<SettingsView>(root).FindName("ProviderProfileEditor"));

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
        if (Application.Current is { } existing && !ReferenceEquals(existing.Dispatcher, Dispatcher.CurrentDispatcher))
            TestHelpers.ResetWpfApplication();

        var application = Application.Current ?? new Application();
        var resources = application.Resources;
        resources.MergedDictionaries.Clear();
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Huaxiazi;component/Resources/Themes/Dark.xaml", UriKind.Relative) });
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Huaxiazi;component/Resources/Styles/GlobalStyles.xaml", UriKind.Relative) });
        resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Huaxiazi;component/Resources/Icons/AppIcons.xaml", UriKind.Relative) });
    }
}
