using System.IO;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class SettingsProductUiTests
{
    [Fact]
    public void ApiSettings_StartsWithAStandaloneProfileManagerAndNavigatesToASeparateEditor()
    {
        var settings = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml"));
        var list = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileListView.xaml"));
        var editor = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileEditView.xaml"));

        Assert.Contains("<local:ProviderProfileListView", settings);
        Assert.Contains("<local:ProviderProfileEditView", settings);
        Assert.DoesNotContain("x:Name=\"ProviderLibraryPanel\"", settings);
        Assert.Contains("Command=\"{Binding AddProviderCommand}\"", list);
        Assert.Contains("Command=\"{Binding DuplicateProviderCommand}\"", list);
        Assert.Contains("Click=\"DeleteSelectedProfile_OnClick\"", list);
        Assert.Contains("Command=\"{Binding NavigateToProviderEditCommand}\"", list);
        Assert.Contains("Command=\"{Binding NavigateToProviderListCommand}\"", editor);
    }

    [Fact]
    public void ProviderEditor_UsesIconAffordancesAndLeavesConnectionChecksToTheProfileManager()
    {
        var editor = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileEditView.xaml"));
        var list = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileListView.xaml"));

        Assert.DoesNotContain("已安全保存", editor);
        Assert.DoesNotContain("获取密钥", editor);
        Assert.DoesNotContain("删除密钥", editor);
        Assert.DoesNotContain("移除密钥", editor);
        Assert.Contains("ToolTip=\"打开厂商密钥页面\"", editor);
        Assert.Contains("ToolTip=\"清空当前输入\"", editor);
        Assert.DoesNotContain("x:Name=\"ConnectionStatusStrip\"", editor);
        Assert.DoesNotContain("Content=\"测试连接\"", editor);
        Assert.Contains("TestProviderProfileCommand", list);
        Assert.Contains("Text=\"{Binding ConnectionStatus}\"", list);
    }

    [Fact]
    public void DisplayControls_WriteBackImmediatelySoValuesAndPreviewStayInSync()
    {
        var settings = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml"));

        Assert.Contains("Value=\"{Binding EditorFontSize, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"", settings);
        Assert.Contains("Value=\"{Binding UiScale, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"", settings);
        Assert.Contains("Value=\"{Binding EditorDefaultHeight, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"", settings);
        Assert.Contains("Value=\"{Binding WindowOpacity, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"", settings);
        Assert.Contains("Value=\"{Binding FloatingBallOpacity, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"", settings);
        Assert.Contains("Value=\"{Binding FloatingBallSize, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"", settings);
    }

    [Fact]
    public void MainEditor_KeepsContextOutOfTheDefaultGuidanceWithoutAContextPanel()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(RepoRoot(), "ViewModels", "MainViewModel.cs"));

        Assert.DoesNotContain("x:Name=\"ContextPopup\"", xaml);
        Assert.DoesNotContain("Header=\"优化上下文\"", xaml);
        Assert.Contains("粘贴或输入要润色的内容", viewModel);
        Assert.DoesNotContain("【上下文】", viewModel);
        Assert.DoesNotContain("【/上下文】", viewModel);
    }

    [Fact]
    public void MainEditor_UsesShortNaturalLanguageGuidanceInsteadOfAnEngineeringTemplate()
    {
        var viewModel = File.ReadAllText(Path.Combine(RepoRoot(), "ViewModels", "MainViewModel.cs"));
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml"));
        var styles = File.ReadAllText(Path.Combine(RepoRoot(), "Resources", "Styles", "GlobalStyles.xaml"));

        Assert.Contains("粘贴或输入要润色的内容", viewModel);
        Assert.Contains("可选：补充对象、目的或语气", viewModel);
        Assert.Contains("描述你想让 AI 完成的任务", viewModel);
        Assert.Contains("可选：补充背景、限制或输出格式", viewModel);
        Assert.Contains("AutomationProperties.HelpText=\"{Binding EditorPlaceholderText}\"", xaml);
        Assert.Contains("TextWrapping=\"Wrap\"", styles);
        Assert.Contains("TextTrimming=\"None\"", styles);
    }

    [Fact]
    public void SettingsView_UsesProductLanguageAndCapturedHotkeys()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml"));

        Assert.DoesNotContain(">Dark<", xaml);
        Assert.DoesNotContain(">WarmOrange<", xaml);
        Assert.Contains("Text=\"{Binding Hotkey, UpdateSourceTrigger=PropertyChanged}\" IsReadOnly=\"True\"", xaml);
        Assert.Contains("PreviewKeyDown=\"HotkeyRecorder_OnPreviewKeyDown\"", xaml);
        Assert.Contains("Content=\"恢复默认\"", xaml);
    }

    [Fact]
    public void SettingsView_UsesSlidersAndContextualHistoryActions()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml"));

        Assert.Contains("x:Name=\"EditorFontSizeSlider\"", xaml);
        Assert.Contains("x:Name=\"WindowOpacitySlider\"", xaml);
        Assert.Contains("IsEnabled=\"{Binding IsFloatingBallSettingsEnabled}\"", xaml);
        Assert.Contains("x:Name=\"ArchiveContextToolbar\"", xaml);
    }

    [Fact]
    public void SettingsView_UsesAutoSaveOnExitAndExposesOptionalEnterToSend()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml"));
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml.cs"));
        var appSettings = File.ReadAllText(Path.Combine(RepoRoot(), "Models", "AppSettings.cs"));
        var main = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml.cs"));

        Assert.Contains("EnterToSend", appSettings);
        Assert.Contains("按 Enter 发送", xaml);
        Assert.Contains("CommitAndCloseAsync", code);
        Assert.Contains("TrySaveAsync", code);
        Assert.Contains("EnterToSend", main);
        Assert.DoesNotContain("有未保存的更改，确定要放弃吗？", code);
        Assert.Contains("验证未通过", code);
        Assert.DoesNotContain("if (!saved) saved = await _vm.TrySaveAsync(saveWithoutVerification: true);", code);
    }

    [Fact]
    public void SettingsExit_DoesNotBlockOnConnectionVerification()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml.cs"));

        Assert.Contains("TrySaveAsync(saveWithoutVerification: true)", code);
        Assert.DoesNotContain("连接验证未通过。仍要保存为草稿吗？", code);
    }

    [Fact]
    public void SettingsView_UsesTheExistingTopRightCloseActionInsteadOfASeparateFooter()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml"));

        Assert.Contains("Click=\"CloseButton_OnClick\"", xaml);
        Assert.DoesNotContain("Content=\"返回\" MinWidth=\"64\"", xaml);
        Assert.DoesNotContain("Content=\"完成\" MinWidth=\"86\"", xaml);
    }

    [Fact]
    public void ApiSettings_ExposeOnlyTheByokHappyPathByDefault()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileEditView.xaml"));

        Assert.Contains("x:Name=\"ProviderPlatformSelector\"", xaml);
        Assert.Contains("GroupStyle", xaml);
        Assert.Contains("x:Name=\"ModelSelector\"", xaml);
        Assert.Contains("Text=\"{Binding ModelId, Mode=OneWay}\"", xaml);
        Assert.Contains("LostFocus=\"ModelSelector_OnLostFocus\"", xaml);
        Assert.DoesNotContain("ApiKeyStateText", xaml);
        Assert.Contains("x:Name=\"ClearApiKeyButton\"", xaml);
        Assert.Contains("Click=\"ClearApiKeyButton_OnClick\"", xaml);
        Assert.DoesNotContain("筛选供应商", xaml);
        Assert.Contains("推理强度", xaml);
        Assert.Contains("ItemsSource=\"{Binding InferenceLevels}\"", xaml);
        Assert.Contains("Header=\"自定义高级参数\"", xaml);
    }

    [Fact]
    public void ApiSettings_ShowCompactStatusWithoutDuplicateSaveActions()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileEditView.xaml"));
        var list = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileListView.xaml"));

        Assert.DoesNotContain("x:Name=\"ConnectionStatusStrip\"", xaml);
        Assert.DoesNotContain("Text=\"{Binding ConnectionStatus}\"", xaml);
        Assert.Contains("Text=\"{Binding ConnectionStatus}\"", list);
        Assert.DoesNotContain("Text=\"{Binding ConnectionDiagnostic}\"", xaml);
        Assert.DoesNotContain("x:Name=\"SaveWithoutVerificationButton\"", xaml);
        Assert.DoesNotContain("CanSaveWithoutVerification, Converter={StaticResource BoolToVisibility}", xaml);
        Assert.DoesNotContain("Text=\"{Binding ConnectionStatus, Mode=OneWay}\" FontFamily=\"Consolas\"", xaml);
    }

    [Fact]
    public void ProviderCards_UseOneAlignedCurrentStateAction()
    {
        var list = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileListView.xaml"));

        Assert.Contains("ProviderInactiveVisibility", list);
        Assert.Contains("Content=\"当前使用\"", list);
        Assert.Contains("Content=\"设为当前\"", list);
    }

    [Fact]
    public void ProviderEditor_NotifiesDraftWhenNameChanges()
    {
        var editor = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileEditView.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileEditView.xaml.cs"));

        Assert.Contains("TextChanged=\"ProviderNameTextBox_OnTextChanged\"", editor);
        Assert.Contains("NotifyProviderProfileEdited", codeBehind);
    }

    [Fact]
    public void SettingsView_UsesOneVisibleValidationBannerAndIndependentPromptCategories()
    {
        var settings = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml"));
        Assert.Contains("x:Name=\"SettingsValidationBanner\"", settings);
        Assert.Contains("Text=\"{Binding ValidationMessage}\"", settings);
        Assert.DoesNotContain("ItemsSource=\"{Binding PromptCategoryOptions}\" IsEnabled=\"{Binding CanConfigureHistoryStorage}\"", settings);
        Assert.DoesNotContain("Margin=\"158,-3,0,8\"", settings);
        Assert.Contains("ScrollViewer.VerticalScrollBarVisibility=\"Auto\"", settings);
    }

    [Fact]
    public void ProviderSubViews_DoNotExposeCompetingSaveModels()
    {
        var list = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileListView.xaml"));
        var listCode = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileListView.xaml.cs"));
        var editor = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileEditView.xaml"));
        var editorCode = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileEditView.xaml.cs"));

        Assert.DoesNotContain("保存更改", list);
        Assert.DoesNotContain("SaveProfiles_OnClick", listCode);
        Assert.DoesNotContain("SaveWithoutVerificationButton", editor);
        Assert.DoesNotContain("Content=\"完成\"", editor);
        Assert.DoesNotContain("SaveWithoutVerificationButton_OnClick", editorCode);
    }

    [Fact]
    public void ApiSettings_UsesAVisibleConfigurationLibraryAndProviderPresetCards()
    {
        var list = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileListView.xaml"));
        var editor = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileEditView.xaml"));

        Assert.Contains("x:Name=\"ProfileListBox\"", list);
        Assert.Contains("Content=\"添加模型\"", list);
        Assert.Contains("Content=\"复制\"", list);
        Assert.Contains("Content=\"编辑\"", list);
        Assert.Contains("Content=\"删除\"", list);
        Assert.Contains("ItemsSource=\"{Binding ProviderPlatformView}\"", editor);
        Assert.Contains("GroupStyle", editor);
    }

    [Fact]
    public void ProviderLibrary_ExposesExplicitCurrentProfileAction()
    {
        var list = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileListView.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(RepoRoot(), "ViewModels", "SettingsViewModel.cs"));

        Assert.Contains("当前使用", list);
        Assert.Contains("SetActiveProviderCommand", list);
        Assert.Contains("ActiveProviderProfileId", viewModel);
        Assert.Contains("SetActiveProvider", viewModel);
    }

    [Fact]
    public void SettingsReplacement_NotifiesTheFloatingWindow()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "App.xaml.cs"));
        var main = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml.cs"));
        var vm = File.ReadAllText(Path.Combine(RepoRoot(), "ViewModels", "MainViewModel.cs"));

        Assert.Contains("SettingsChanged", app);
        Assert.Contains("App.SettingsChanged", main);
        Assert.Contains("OnPropertyChanged(nameof(ProviderProfiles))", vm);
        Assert.Contains("OnPropertyChanged(nameof(ActiveProviderLabel))", vm);
    }

    [Fact]
    public void ModelControl_CombinesRecommendedSelectionWithEditableModelId()
    {
        var settingsXaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileEditView.xaml"));
        var globalStyles = File.ReadAllText(Path.Combine(RepoRoot(), "Resources", "Styles", "GlobalStyles.xaml"));

        Assert.Contains("IsEditable=\"True\"", settingsXaml);
        Assert.Contains("TextSearch.TextPath=\"ModelId\"", settingsXaml);
        Assert.Contains("Text=\"{Binding ModelId, Mode=OneWay}\"", settingsXaml);
        Assert.Contains("LostFocus=\"ModelSelector_OnLostFocus\"", settingsXaml);
        Assert.Contains("SelectionChanged=\"ModelSelector_OnSelectionChanged\"", settingsXaml);
        Assert.DoesNotContain("x:Name=\"ModelIdTextBox\"", settingsXaml);
        Assert.Contains("x:Name=\"PART_EditableTextBox\"", globalStyles);
        Assert.Contains("Property=\"IsEditable\" Value=\"True\"", globalStyles);
    }

    [Fact]
    public void ApiSettings_UsesOneScrollOwnerAndAConsistentCompactRhythm()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml"));
        var list = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileListView.xaml"));
        var styles = File.ReadAllText(Path.Combine(RepoRoot(), "Resources", "Styles", "GlobalStyles.xaml"));

        Assert.Contains("x:Name=\"SettingsPageScrollViewer\"", xaml);
        Assert.Contains("local:NestedScrollBehavior.ForwardMouseWheelToParent=\"True\"", list);
        Assert.Contains("x:Key=\"SettingsStepTitle\"", xaml);
        Assert.Contains("x:Key=\"SettingsSupportingText\"", xaml);
        Assert.Contains("x:Key=\"CompactSettingsField\"", styles);
        Assert.DoesNotContain("<Setter Property=\"Width\" Value=\"132\" />", xaml);
        Assert.DoesNotContain("<Grid MinHeight=\"560\">", xaml);
    }

    [Fact]
    public void ArchiveList_UsesHuaxiaziScrollChromeAndKeepsThePageAsTheScrollOwner()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml"));

        Assert.Contains("x:Name=\"ArchiveList\"", xaml);
        Assert.Contains("BasedOn=\"{StaticResource OverlayScrollViewer}\"", xaml);
        Assert.Contains("ScrollViewer.HorizontalScrollBarVisibility=\"Disabled\"", xaml);
        Assert.Contains("local:NestedScrollBehavior.ForwardMouseWheelToParent=\"True\"", xaml);
    }

    [Fact]
    public void SettingsView_KeepsKeyboardFocusVisible()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml"));
        Assert.Contains("x:Key=\"SettingsFocusVisual\"", xaml);
        Assert.DoesNotContain("<Setter Property=\"FocusVisualStyle\" Value=\"{x:Null}\" />", xaml);
    }

    [Fact]
    public void SettingsView_NumericFieldsRejectInvalidTextAndDeclareTheirRanges()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml"));
        var editor = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileEditView.xaml"));

        Assert.Contains("local:IntegerInputBehavior.Minimum=\"1\" local:IntegerInputBehavior.Maximum=\"100\"", xaml);
        Assert.Contains("local:IntegerInputBehavior.Minimum=\"1\" local:IntegerInputBehavior.Maximum=\"3650\"", xaml);
        Assert.Contains("local:IntegerInputBehavior.Minimum=\"250\" local:IntegerInputBehavior.Maximum=\"10000\"", xaml);
        Assert.Contains("local:IntegerInputBehavior.Minimum=\"10\" local:IntegerInputBehavior.Maximum=\"600\"", editor);
    }

    [Fact]
    public void ExpressionSettings_UseOneProgressivelyDisclosedAbilityPage()
    {
        var settings = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml"));
        var ability = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ExpressionAbilitySettingsView.xaml"));

        Assert.Contains("ConverterParameter=表达与生成", settings);
        Assert.Contains("ConverterParameter=外观与窗口", settings);
        Assert.Contains("ConverterParameter=历史与留存", settings);
        Assert.Contains("ConverterParameter=数据维护", settings);
        Assert.Contains("Width=\"200\"", settings);
        Assert.Contains("MaxWidth=\"780\"", settings);
        Assert.Contains("永久清空历史与缓存…", settings);
        Assert.Contains("x:Key=\"SettingsSlider\"", settings);
        Assert.Contains("DisplayPreviewSummary", settings);
        Assert.Contains("ElementName=EditorFontSizeSlider", settings);
        Assert.Contains("ElementName=UiScaleSlider", settings);
        Assert.Contains("ElementName=EditorDefaultHeightSlider", settings);
        Assert.Contains("ElementName=WindowOpacitySlider", settings);
        Assert.Contains("ElementName=FloatingBallOpacitySlider", settings);
        Assert.Contains("ElementName=FloatingBallSizeSlider", settings);
        Assert.Contains("Margin=\"28,18,12,0\"", settings);
        Assert.Contains("Padding=\"0,0,18,16\"", settings);
        Assert.Contains("ScrollContentPresenter CanContentScroll=\"{TemplateBinding CanContentScroll}\" Margin=\"{TemplateBinding Padding}\"", File.ReadAllText(Path.Combine(RepoRoot(), "Resources", "Styles", "GlobalStyles.xaml")));
        Assert.DoesNotContain("ConverterParameter=表达偏好", settings);
        Assert.DoesNotContain("ConverterParameter=表达策略", settings);
        Assert.Contains("x:Name=\"ExpressionOverviewPane\"", ability);
        Assert.Contains("Text=\"生成默认值\"", ability);
        Assert.Contains("Text=\"主界面显示\"", ability);
        Assert.Contains("Text=\"我的风格与身份\"", ability);
        Assert.Contains("Text=\"自定义规则（高级）\"", ability);
        Assert.Contains("x:Name=\"ExpressionSkillsPane\"", ability);
        Assert.Contains("x:Name=\"OpenSkillsButton\"", ability);
        Assert.Contains("x:Name=\"SkillDetailsScrollViewer\"", ability);
        Assert.Contains("x:Name=\"SkillColumnsFrame\" Grid.Row=\"2\" Height=\"440\"", ability);
        var abilityCode = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ExpressionAbilitySettingsView.xaml.cs"));
        Assert.Contains("ShowDialog(Window.GetWindow(this))", abilityCode);
        Assert.Contains("InspectAgentSkillAsync", abilityCode);
        Assert.DoesNotContain("ExternalStrategiesEnabled", ability);
        Assert.DoesNotContain("启用表达润色", ability);
        Assert.DoesNotContain("启用提示词优化", ability);
        Assert.DoesNotContain("LegacyExpressionSettingsRetainedForBindingCompatibility", settings);
        Assert.Contains("x:Name=\"CustomSystemPromptTextBox\"", ability);
        Assert.Contains("x:Name=\"OptimizationPresetList\"", ability);
        Assert.DoesNotContain("SkillManagerExpander", ability);
        Assert.Contains("IsStrategiesLoading", ability);
    }

    [Fact]
    public void ExpressionSettings_UseChineseModeLabelsAndMakeMeaningRuleNonConfigurable()
    {
        var ability = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ExpressionAbilitySettingsView.xaml"));
        var settings = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "SettingsView.xaml"));

        Assert.Contains("ItemsSource=\"{Binding Modes}\"", ability);
        Assert.Contains("Converter={StaticResource EnumDisplayName}", ability);
        Assert.DoesNotContain("Content=\"保留原意\"", ability);
        Assert.Contains("事实保真由内置规则保护", ability);
        Assert.Contains("Text=\"提示词优化\"", settings);
        Assert.DoesNotContain("Text=\"Prompt 优化\"", settings);
    }

    [Fact]
    public void AppSettings_DoNotExposeUnconsumedStrategySwitches()
    {
        var appSettings = File.ReadAllText(Path.Combine(RepoRoot(), "Models", "AppSettings.cs"));

        Assert.DoesNotContain("ExternalStrategiesEnabled", appSettings);
        Assert.DoesNotContain("PolishStrategyId", appSettings);
        Assert.DoesNotContain("PromptStrategyId", appSettings);
    }

    [Fact]
    public void ApiSettings_ShowTheActualConfigurationFieldsWithoutRepeatedMicrocopy()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileEditView.xaml"));

        Assert.Contains("x:Name=\"ProviderNameTextBox\"", xaml);
        Assert.Contains("x:Name=\"ProviderRemarkTextBox\"", xaml);
        Assert.Contains("x:Name=\"ApiBaseTextBox\"", xaml);
        Assert.Contains("Text=\"{Binding ApiBaseInput, UpdateSourceTrigger=PropertyChanged}\"", xaml);
        Assert.DoesNotContain("官方预设", xaml);
        Assert.DoesNotContain("选择后立即编辑", xaml);
        Assert.DoesNotContain("ProviderVerificationText", xaml);
        Assert.DoesNotContain("ProviderOfficialUrlTextBox", xaml);
    }

    [Fact]
    public void MainWindow_ModeSwitcherUsesCompactStableSegments()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml"));

        Assert.Contains("x:Name=\"ModeSwitcher\" Width=\"92\" Height=\"28\"", xaml);
        Assert.Contains("x:Name=\"PolishModeLabel\" Text=\"润色\"", xaml);
        Assert.Contains("x:Name=\"PromptModeLabel\" Text=\"提示词\"", xaml);
        Assert.Contains("x:Name=\"ModeMorphTransform\"", xaml);
        Assert.DoesNotContain("x:Name=\"ModeTransitionLabels\"", xaml);
        Assert.DoesNotContain("x:Name=\"ModeCarouselButton\"", xaml);
        Assert.DoesNotContain("x:Name=\"ModeCarouselLayout\"", xaml);
        Assert.DoesNotContain("x:Name=\"ModeMorphSpark\"", xaml);
        Assert.Contains("FontFamily=\"{StaticResource AppFont}\"", xaml);
        Assert.DoesNotContain("x:Name=\"PolishModeLabel\" Text=\"润色\" FontFamily=\"{StaticResource ButtonFont}\"", xaml);
        Assert.DoesNotContain("x:Name=\"PromptModeLabel\" Text=\"提示词\" FontFamily=\"{StaticResource ButtonFont}\"", xaml);
        // 文字使用应用统一字体，并居中于等宽分段。
        Assert.Contains("HorizontalAlignment=\"Center\"", xaml);
        Assert.DoesNotContain("x:Name=\"PolishModeLabel\" Text=\"润色\" FontFamily=\"{StaticResource AppFont}\" FontSize=\"12\" FontWeight=\"Bold\"", xaml);
        Assert.DoesNotContain("x:Name=\"PromptModeLabel\" Text=\"提示词\" FontFamily=\"{StaticResource AppFont}\" FontSize=\"12\" FontWeight=\"Bold\"", xaml);
        Assert.Contains("Command=\"{Binding SelectModeCommand}\"", xaml);
        Assert.Contains("CommandParameter=\"{x:Static models:ApplicationMode.Polish}\"", xaml);
        Assert.Contains("CommandParameter=\"{x:Static models:ApplicationMode.PromptOptimize}\"", xaml);
        Assert.Contains("Click=\"ModeCarouselButton_OnClick\"", xaml);
        Assert.DoesNotContain("x:Name=\"PolishModeButton\"", xaml);
        Assert.DoesNotContain("x:Name=\"PromptModeButton\"", xaml);
    }

    [Fact]
    public void NativeUi_UsesReadableInputMetricsAndAvoidsTinyStatusText()
    {
        var styles = File.ReadAllText(Path.Combine(RepoRoot(), "Resources", "Styles", "GlobalStyles.xaml"));
        var mainWindow = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml"));

        Assert.Contains("<Setter Property=\"Padding\" Value=\"10,6\" />", styles);
        Assert.Contains("<ScrollViewer x:Name=\"PART_ContentHost\" Margin=\"{TemplateBinding Padding}\" />", styles);
        Assert.DoesNotContain("FontSize=\"8\"", mainWindow);
        Assert.DoesNotContain("FontSize=\"9\"", mainWindow);
    }

    [Fact]
    public void ProviderEditor_HeaderUsesFlowLayoutInsteadOfAbsoluteOffsets()
    {
        var editor = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ProviderProfileEditView.xaml"));

        Assert.Contains("x:Name=\"ProviderEditorHeader\"", editor);
        Assert.Contains("x:Name=\"ProviderEditorTitle\"", editor);
        Assert.Contains("x:Name=\"ProviderEditorBreadcrumb\"", editor);
        Assert.DoesNotContain("Margin=\"80,0,0,12\"", editor);
        Assert.DoesNotContain("Margin=\"156,3,0,12\"", editor);
    }

    [Fact]
    public void NativeControls_KeepFocusVisibleAndGiveSwitchesAStableHitArea()
    {
        var styles = File.ReadAllText(Path.Combine(RepoRoot(), "Resources", "Styles", "GlobalStyles.xaml"));
        var mainWindow = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml"));

        Assert.Contains("x:Key=\"KeyboardFocusVisual\"", styles);
        Assert.Contains("FocusVisualStyle\" Value=\"{StaticResource KeyboardFocusVisual}\"", styles);
        Assert.Contains("x:Key=\"MainWindowFocusVisual\"", mainWindow);
        Assert.DoesNotContain("FocusVisualStyle\" Value=\"{x:Null}\"", mainWindow);
        Assert.Contains("x:Key=\"SwitchToggle\" TargetType=\"ToggleButton\"", styles);
        Assert.Contains("<Setter Property=\"Width\" Value=\"40\" />", styles);
        Assert.Contains("<Setter Property=\"Height\" Value=\"24\" />", styles);
        Assert.Contains("x:Name=\"Track\" Width=\"34\" Height=\"18\"", styles);
    }

    [Fact]
    public void ModeSwitcher_DoesNotFlipOnAmbientMouseWheelInput()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml.cs"));

        Assert.Contains("ModeSwitcher.IsKeyboardFocusWithin", code);
        Assert.Contains("if (!ModeSwitcher.IsKeyboardFocusWithin) return;", code);
    }

    [Fact]
    public void ExpressionOverview_MakesSkillManagementAFirstClassAction()
    {
        var ability = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "ExpressionAbilitySettingsView.xaml"));

        Assert.Contains("Content=\"管理能力\"", ability);
        Assert.Contains("Style=\"{StaticResource PrimaryButton}\"", ability);
    }

    private static string RepoRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Huaxiazi.sln")))
            directory = Path.GetDirectoryName(directory);
        return directory ?? throw new DirectoryNotFoundException("未找到解决方案根目录。");
    }
}
