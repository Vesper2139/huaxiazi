using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptFloat.Models;
using PromptFloat.Services;

namespace PromptFloat.ViewModels;

/// <summary>连接测试状态类别（用于颜色编码反馈）。</summary>
public enum ConnectionStatusKind
{
    Neutral,
    Pending,
    Success,
    Failure
}

/// <summary>配置管理首页与详情页的导航状态。</summary>
public enum ProviderProfileViewMode
{
    List,
    Edit
}

/// <summary>表达能力页的局部路由。概览与 Skill 管理是两个独立的用户任务。</summary>
public enum ExpressionAbilityPane
{
    Overview,
    Skills
}

/// <summary>当前选中配置的 API Key 视觉状态。</summary>
public enum ApiKeyStatusKind
{
    Set,
    Missing,
    NotRequired
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ArchiveService _archiveService;
    private readonly ISecretStore _secretStore;
    private readonly Func<ProviderProfile, string?, CancellationToken, Task<ConnectionTestResult>> _connectionTester;
    private readonly Func<IReadOnlyList<AgentSkillRecord>>? _skillCatalogLoader;
    private readonly Dictionary<string, string?> _pendingApiKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removedSecretIds = new(StringComparer.Ordinal);
    private readonly DataManagementService _dataManagement = new();
    private readonly AppSettings _originalDisplaySettings;
    public IReadOnlyList<string> Sections { get; } =
        ["模型与 API", "表达能力", "历史与会话", "界面与显示", "窗口与行为", "快捷键", "数据管理", "关于与更新"];
    [ObservableProperty] private string _selectedSection = "模型与 API";
    [ObservableProperty] private ExpressionAbilityPane _expressionAbilityPane = ExpressionAbilityPane.Overview;
    public IReadOnlyList<PromptCategory> Categories => PromptCategoryMetadata.AllCategories;
    public IReadOnlyList<PromptDepth> Depths => PromptDepthMetadata.AllDepths;
    public IReadOnlyList<ApplicationMode> Modes { get; } = [ApplicationMode.Polish, ApplicationMode.PromptOptimize];
    public IReadOnlyList<string> OutputStyles { get; } = ["自然", "克制", "亲切", "专业", "正式", "简洁"];
    public IReadOnlyList<string> PolishScenarios { get; } = ["私人沟通", "职场沟通", "公开发布", "正式材料", "其他"];
    public IReadOnlyList<SettingOption> ThemeModes { get; } =
        [new("System", "跟随 Windows"), new("Light", "浅色"), new("Dark", "深色")];
    public IReadOnlyList<CompanionDriverModeOption> CompanionDriverModes { get; } =
    [
        new(CompanionDriverMode.Local, "本地响应（推荐）", "仅根据鼠标、窗口、编辑和生成结果驱动动画，零额外 Token。"),
        new(CompanionDriverMode.EmotionAssistant, "情绪助手", "不增加额外请求；会在原生成中增加少量提示词与输出 Token。")
    ];
    public ObservableCollection<SettingOption> SpecialSkins { get; } =
        [new("default", "默认外观"), new("LuoXiaoHei", "罗小黑"), new("MaoDie", "耄耋")];
    public IReadOnlyList<SettingOption> CloseBehaviors { get; } =
        [new("Hide", "收缩为悬浮球"), new("Tray", "隐藏到托盘"), new("Exit", "退出程序")];
    public IReadOnlyList<SettingOption> EscapeBehaviors { get; } =
        [new("Hide", "收缩为悬浮球"), new("Tray", "隐藏到托盘"), new("None", "不执行操作")];
    public IReadOnlyList<ProviderPlatformOption> AllProviderPlatforms => ProviderPlatformCatalog.Options;
    public IReadOnlyList<ProviderPlatformOption> CommonProviderPlatforms { get; } = ProviderPlatformCatalog.Options
        .Where(option => option.Tier == ProviderPresetTier.Common)
        .ToList();
    public ListCollectionView ProviderPlatformView { get; }

    /// <summary>供应商列表：按 ProviderSearchText 过滤（始终保留当前选中项）。</summary>
    public IReadOnlyList<ProviderPlatformOption> ProviderPlatforms
    {
        get
        {
            var search = ProviderSearchText?.Trim();
            if (string.IsNullOrWhiteSpace(search)) return ProviderPlatformCatalog.Options;
            var selected = SelectedProviderPlatform;
            return ProviderPlatformCatalog.Options
                .Where(option => option.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) || ReferenceEquals(option, selected))
                .ToList();
        }
    }

    private string _providerSearchText = string.Empty;
    public string ProviderSearchText
    {
        get => _providerSearchText;
        set
        {
            if (SetProperty(ref _providerSearchText, value)) OnPropertyChanged(nameof(ProviderPlatforms));
        }
    }

    // ===== 配置管理首页（列表）状态 =====

    [ObservableProperty] private ProviderProfileViewMode _providerViewMode = ProviderProfileViewMode.List;
    [ObservableProperty] private ProviderProfile? _editingProviderProfile;

    private string _providerListSearchText = string.Empty;
    public string ProviderListSearchText
    {
        get => _providerListSearchText;
        set
        {
            if (SetProperty(ref _providerListSearchText, value))
                OnPropertyChanged(nameof(FilteredProviderProfiles));
        }
    }

    public IReadOnlyList<ProviderProfile> FilteredProviderProfiles
    {
        get
        {
            var search = ProviderListSearchText?.Trim();
            if (string.IsNullOrWhiteSpace(search)) return ProviderProfiles.ToList();
            return ProviderProfiles
                .Where(p => p.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                            ProviderPlatformCatalog.Get(p.Platform).DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    public bool CanDuplicateOrEditProvider => SelectedProviderProfile is not null;
    public bool CanRemoveProvider => ProviderProfiles.Count > 1 && SelectedProviderProfile is not null;

    [ObservableProperty] private bool _isDiagnosticExpanded;

    public ApiKeyStatusKind ApiKeyStatusKind
    {
        get
        {
            if (SelectedProviderProfile is null) return ApiKeyStatusKind.Missing;
            if (SelectedProviderPlatform?.RequiresApiKey == false) return ApiKeyStatusKind.NotRequired;
            if (_pendingApiKeys.TryGetValue(SelectedProviderProfile.SecretId, out var pending))
                return pending is null ? ApiKeyStatusKind.Missing : ApiKeyStatusKind.Set;
            return _secretStore.Exists(SelectedProviderProfile.SecretId) ? ApiKeyStatusKind.Set : ApiKeyStatusKind.Missing;
        }
    }

    /// <summary>计算任意配置的 API Key 状态（供列表状态点使用）。</summary>
    public ApiKeyStatusKind GetApiKeyStatusKind(ProviderProfile profile)
    {
        var option = ProviderPlatformCatalog.Get(profile.Platform);
        if (!option.RequiresApiKey) return ApiKeyStatusKind.NotRequired;
        if (_pendingApiKeys.TryGetValue(profile.SecretId, out var pending))
            return pending is null ? ApiKeyStatusKind.Missing : ApiKeyStatusKind.Set;
        return _secretStore.Exists(profile.SecretId) ? ApiKeyStatusKind.Set : ApiKeyStatusKind.Missing;
    }

    public IReadOnlyList<ArchiveModeFilterOption> ArchiveModeFilters { get; } =
        [new("全部模式", null), new("表达润色", ApplicationMode.Polish), new("提示词优化", ApplicationMode.PromptOptimize)];
    public IReadOnlyList<ArchiveExportFormat> ExportFormats { get; } =
        [ArchiveExportFormat.Text, ArchiveExportFormat.Markdown, ArchiveExportFormat.Json];
    public string CurrentVersion => UpdateChecker.GetCurrentVersion().ToString(3);

    public ObservableCollection<ProviderProfile> ProviderProfiles { get; } = [];
    public ObservableCollection<ContentRevision> ArchiveItems { get; } = [];
    public ObservableCollection<PromptCategoryOption> PromptCategoryOptions { get; } = [];
    public ObservableCollection<OptimizationPreset> OptimizationPresets { get; } = [];
    public ObservableCollection<HealthCheckItem> HealthItems { get; } = [];

    private PromptCategory _defaultCategory;
    public PromptCategory DefaultCategory { get => _defaultCategory; set => SetDirty(ref _defaultCategory, value); }
    private PromptDepth _defaultDepth;
    public PromptDepth DefaultDepth { get => _defaultDepth; set => SetDirty(ref _defaultDepth, value); }
    private int _promptHistoryLimit = 20;
    public int PromptHistoryLimit { get => _promptHistoryLimit; set => SetDirty(ref _promptHistoryLimit, value); }
    private string _hotkey = string.Empty;
    public string Hotkey { get => _hotkey; set { SetDirty(ref _hotkey, value); ValidateHotkeysLive(); } }
    private string _quickPolishHotkey = string.Empty;
    public string QuickPolishHotkey { get => _quickPolishHotkey; set { SetDirty(ref _quickPolishHotkey, value); ValidateHotkeysLive(); } }
    private string _quickPromptHotkey = string.Empty;
    public string QuickPromptHotkey { get => _quickPromptHotkey; set { SetDirty(ref _quickPromptHotkey, value); ValidateHotkeysLive(); } }
    private string _copyResultHotkey = string.Empty;
    public string CopyResultHotkey { get => _copyResultHotkey; set { SetDirty(ref _copyResultHotkey, value); ValidateHotkeysLive(); } }
    private ApplicationMode _defaultMode;
    public ApplicationMode DefaultMode { get => _defaultMode; set => SetDirty(ref _defaultMode, value); }
    private bool _clipboardAutoRead;
    public bool ClipboardAutoRead { get => _clipboardAutoRead; set => SetDirty(ref _clipboardAutoRead, value); }
    private bool _startWithWindows;
    public bool StartWithWindows { get => _startWithWindows; set => SetDirty(ref _startWithWindows, value); }
    private bool _floatingBallEnabled;
    public bool FloatingBallEnabled
    {
        get => _floatingBallEnabled;
        set
        {
            if (_floatingBallEnabled == value) return;
            SetDirty(ref _floatingBallEnabled, value);
            OnPropertyChanged(nameof(IsFloatingBallSettingsEnabled));
        }
    }
    public bool IsFloatingBallSettingsEnabled => FloatingBallEnabled;
    private bool _trayEnabled;
    public bool TrayEnabled
    {
        get => _trayEnabled;
        set
        {
            if (_trayEnabled == value) return;
            SetDirty(ref _trayEnabled, value);
            if (value) return;
            if (CloseBehavior == "Tray") CloseBehavior = "Hide";
            if (EscapeBehavior == "Tray") EscapeBehavior = "Hide";
        }
    }
    private bool _alwaysOnTop;
    public bool AlwaysOnTop { get => _alwaysOnTop; set => SetDirty(ref _alwaysOnTop, value); }
    private bool _autoArchive;
    public bool AutoArchive { get => _autoArchive; set => SetDirty(ref _autoArchive, value); }
    private bool _historyEnabled = true;
    public bool HistoryEnabled
    {
        get => _historyEnabled;
        set
        {
            if (_historyEnabled == value) return;
            SetDirty(ref _historyEnabled, value);
            OnPropertyChanged(nameof(CanConfigureHistoryStorage));
        }
    }
    private int _historyRetentionDays = 30;
    public int HistoryRetentionDays { get => _historyRetentionDays; set => SetDirty(ref _historyRetentionDays, value); }
    private bool _saveOriginalText = true;
    public bool SaveOriginalText { get => _saveOriginalText; set => SetDirty(ref _saveOriginalText, value); }
    private bool _saveOptimizedText = true;
    public bool SaveOptimizedText { get => _saveOptimizedText; set => SetDirty(ref _saveOptimizedText, value); }
    private bool _incognitoMode;
    public bool IncognitoMode
    {
        get => _incognitoMode;
        set
        {
            if (_incognitoMode == value) return;
            SetDirty(ref _incognitoMode, value);
            OnPropertyChanged(nameof(CanConfigureHistoryStorage));
        }
    }
    public bool CanConfigureHistoryStorage => HistoryEnabled && !IncognitoMode;
    private bool _autoCopyAfterOptimize;
    public bool AutoCopyAfterOptimize { get => _autoCopyAfterOptimize; set => SetDirty(ref _autoCopyAfterOptimize, value); }
    private bool _enterToSend;
    public bool EnterToSend { get => _enterToSend; set => SetDirty(ref _enterToSend, value); }
    private int _autosaveDelayMilliseconds = 750;
    public int AutosaveDelayMilliseconds { get => _autosaveDelayMilliseconds; set => SetDirty(ref _autosaveDelayMilliseconds, value); }
    private bool _clarificationEnabled;
    public bool ClarificationEnabled { get => _clarificationEnabled; set => SetDirty(ref _clarificationEnabled, value); }
    private bool _showDiff;
    public bool ShowDiff { get => _showDiff; set => SetDirty(ref _showDiff, value); }
    private string _defaultPolishScenario = "其他";
    public string DefaultPolishScenario { get => _defaultPolishScenario; set => SetDirty(ref _defaultPolishScenario, value); }
    private string _persona = string.Empty;
    public string Persona { get => _persona; set => SetDirty(ref _persona, value); }
    private string _outputStyle = "自然";
    public string OutputStyle
    {
        get => _outputStyle;
        set
        {
            if (string.Equals(_outputStyle, value, StringComparison.Ordinal)) return;
            SetDirty(ref _outputStyle, value);
            if (App.Settings.PreferenceLearningEnabled && !App.Settings.IncognitoMode)
                new StructuredPreferenceService().RecordStyleChoice(App.Settings.ExpressionPreferenceProfile);
        }
    }
    private string _customStyleInstructions = string.Empty;
    public string CustomStyleInstructions { get => _customStyleInstructions; set => SetDirty(ref _customStyleInstructions, value); }
    private string _customSystemPrompt = string.Empty;
    public string CustomSystemPrompt { get => _customSystemPrompt; set => SetDirty(ref _customSystemPrompt, value); }
    private bool _preferenceLearningEnabled = true;
    public bool PreferenceLearningEnabled { get => _preferenceLearningEnabled; set => SetDirty(ref _preferenceLearningEnabled, value); }
    public string PreferenceSummary
    {
        get
        {
            var profile = App.Settings.ExpressionPreferenceProfile ?? new ExpressionPreferenceProfile();
            if (profile.EditCount == 0) return "尚未形成偏好；只会保存统计值，不保存学习样本文本。";
            var direction = profile.ShorteningEdits > profile.ExpansionEdits ? "偏好更简洁" : "偏好结构完整";
            return $"已从 {profile.EditCount} 次主动编辑中形成摘要：{direction}。";
        }
    }

    [RelayCommand]
    private void ResetExpressionPreferences()
    {
        App.Settings.ExpressionPreferenceProfile = new ExpressionPreferenceProfile();
        try { App.ConfigService.Save(App.Settings); } catch { }
        OnPropertyChanged(nameof(PreferenceSummary));
        ValidationMessage = "本地表达偏好已重置。";
    }
    private bool _preserveMeaning = true;
    public bool PreserveMeaning { get => _preserveMeaning; set => SetDirty(ref _preserveMeaning, value); }
    private bool _minimalRewrite;
    public bool MinimalRewrite { get => _minimalRewrite; set => SetDirty(ref _minimalRewrite, value); }
    private bool _professionalTone;
    public bool ProfessionalTone { get => _professionalTone; set => SetDirty(ref _professionalTone, value); }
    private OptimizationPreset? _selectedOptimizationPreset;
    public OptimizationPreset? SelectedOptimizationPreset { get => _selectedOptimizationPreset; set => SetProperty(ref _selectedOptimizationPreset, value); }
    private string _themeMode = "System";
    public string ThemeMode
    {
        get => _themeMode;
        set { SetDirty(ref _themeMode, value); ApplyPreview(); }
    }
    private string _skinId = "default";
    public string SkinId
    {
        get => _skinId;
        set
        {
            if (_skinId == value) return;
            SetDirty(ref _skinId, value);
            OnPropertyChanged(nameof(IsDefaultSkinSelected));
            OnPropertyChanged(nameof(CanUninstallSelectedSkin));
            ApplyPreview();
        }
    }
    public bool IsDefaultSkinSelected => string.Equals(SkinId, "default", StringComparison.OrdinalIgnoreCase);
    public bool CanUninstallSelectedSkin => App.SkinService.GetSkin(SkinId) is { IsBuiltIn: false };

    public void RegisterImportedSkin(SkinManifest manifest)
    {
        App.SkinService.Register(manifest);
        if (!SpecialSkins.Any(option => string.Equals(option.Value, manifest.Id, StringComparison.OrdinalIgnoreCase)))
            SpecialSkins.Add(new SettingOption(manifest.Id, manifest.DisplayName));
        SkinId = manifest.Id;
    }

    public void UninstallSelectedSkin()
    {
        if (App.SkinService.GetSkin(SkinId) is not { IsBuiltIn: false } manifest) return;
        SkinId = "default";
        new SkinPackageService(Path.Combine(App.DataRoot, "skins")).Uninstall(manifest);
        App.SkinService.Unregister(manifest.Id);
        var option = SpecialSkins.FirstOrDefault(item => string.Equals(item.Value, manifest.Id, StringComparison.OrdinalIgnoreCase));
        if (option is not null) SpecialSkins.Remove(option);
    }
    private double _editorFontSize = 13;
    public double EditorFontSize { get => _editorFontSize; set { SetDirty(ref _editorFontSize, value); OnPropertyChanged(nameof(DisplayPreviewSummary)); ApplyPreview(); } }
    private double _uiScale = 1;
    public double UiScale { get => _uiScale; set { SetDirty(ref _uiScale, value); OnPropertyChanged(nameof(DisplayPreviewSummary)); ApplyPreview(); } }
    private double _editorDefaultHeight = 120;
    public double EditorDefaultHeight { get => _editorDefaultHeight; set { SetDirty(ref _editorDefaultHeight, value); OnPropertyChanged(nameof(DisplayPreviewSummary)); ApplyPreview(); } }
    private bool _animationsEnabled = true;
    public bool AnimationsEnabled { get => _animationsEnabled; set { SetDirty(ref _animationsEnabled, value); ApplyPreview(); } }
    private CompanionDriverMode _companionDriverMode = CompanionDriverMode.Local;
    public CompanionDriverMode CompanionDriverMode { get => _companionDriverMode; set => SetDirty(ref _companionDriverMode, value); }
    private double _windowOpacity = 1;
    public double WindowOpacity { get => _windowOpacity; set { SetDirty(ref _windowOpacity, value); OnPropertyChanged(nameof(DisplayPreviewSummary)); ApplyPreview(); } }
    private double _floatingBallOpacity = 0.92;
    public double FloatingBallOpacity { get { return _floatingBallOpacity; } set { SetDirty(ref _floatingBallOpacity, value); OnPropertyChanged(nameof(DisplayPreviewSummary)); ApplyPreview(); } }
    private double _floatingBallSize = 40;
    public double FloatingBallSize { get { return _floatingBallSize; } set { SetDirty(ref _floatingBallSize, value); OnPropertyChanged(nameof(DisplayPreviewSummary)); ApplyPreview(); } }
    public string DisplayPreviewSummary =>
        $"字号 {EditorFontSize:0} px  ·  缩放 {UiScale:P0}  ·  编辑框 {EditorDefaultHeight:0} px  ·  窗口透明度 {WindowOpacity:P0}  ·  悬浮球 {FloatingBallSize:0} px / {FloatingBallOpacity:P0}";
    private string _closeBehavior = "Hide";
    public string CloseBehavior
    {
        get => _closeBehavior;
        set { SetDirty(ref _closeBehavior, value); if (value == "Tray") TrayEnabled = true; }
    }
    private string _escapeBehavior = "Hide";
    public string EscapeBehavior
    {
        get => _escapeBehavior;
        set { SetDirty(ref _escapeBehavior, value); if (value == "Tray") TrayEnabled = true; }
    }
    private bool _rememberWindowSize = true;
    public bool RememberWindowSize { get => _rememberWindowSize; set => SetDirty(ref _rememberWindowSize, value); }
    private bool _rememberFloatingBallPosition = true;
    public bool RememberFloatingBallPosition { get => _rememberFloatingBallPosition; set => SetDirty(ref _rememberFloatingBallPosition, value); }
    private bool _snapFloatingBallToEdge = true;
    public bool SnapFloatingBallToEdge { get => _snapFloatingBallToEdge; set => SetDirty(ref _snapFloatingBallToEdge, value); }
    private bool _showInTaskbar;
    public bool ShowInTaskbar { get => _showInTaskbar; set => SetDirty(ref _showInTaskbar, value); }
    private bool _hasChanges;
    public bool HasChanges { get => _hasChanges; set => SetProperty(ref _hasChanges, value); }
    private string _validationMessage = string.Empty;
    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (!SetProperty(ref _validationMessage, value)) return;
            OnPropertyChanged(nameof(HasValidationMessage));
        }
    }
    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);
    private string _archiveSearch = string.Empty;
    public string ArchiveSearch { get => _archiveSearch; set => SetProperty(ref _archiveSearch, value); }
    private bool _showDeleted;
    public bool ShowDeleted { get => _showDeleted; set => SetProperty(ref _showDeleted, value); }
    private ContentRevision? _selectedArchiveItem;
    public ContentRevision? SelectedArchiveItem
    {
        get => _selectedArchiveItem;
        set
        {
            if (!SetProperty(ref _selectedArchiveItem, value)) return;
            OnPropertyChanged(nameof(HasSelectedArchiveItem));
            OnPropertyChanged(nameof(FavoriteArchiveActionDisplay));
            OnPropertyChanged(nameof(ArchiveItemActionDisplay));
        }
    }
    public bool HasSelectedArchiveItem => SelectedArchiveItem is not null;
    public string FavoriteArchiveActionDisplay => SelectedArchiveItem?.IsFavorite == true ? "取消收藏" : "收藏";
    public string ArchiveItemActionDisplay => SelectedArchiveItem?.IsArchived == true ? "取消归档" : "归档";
    private string _dataStatus = string.Empty;
    public string DataStatus { get => _dataStatus; set => SetProperty(ref _dataStatus, value); }
    [ObservableProperty] private string _archiveValidationMessage = string.Empty;
    private string _archiveExportDirectory = string.Empty;
    public string ArchiveExportDirectory { get => _archiveExportDirectory; set => SetProperty(ref _archiveExportDirectory, value); }
    private ArchiveModeFilterOption _archiveModeFilter = new("全部模式", null);
    public ArchiveModeFilterOption ArchiveModeFilter { get => _archiveModeFilter; set => SetProperty(ref _archiveModeFilter, value); }
    private string _archiveFromText = string.Empty;
    public string ArchiveFromText { get => _archiveFromText; set => SetProperty(ref _archiveFromText, value); }
    private string _archiveToText = string.Empty;
    public string ArchiveToText { get => _archiveToText; set => SetProperty(ref _archiveToText, value); }
    private ArchiveExportFormat _selectedExportFormat = ArchiveExportFormat.Markdown;
    public ArchiveExportFormat SelectedExportFormat { get => _selectedExportFormat; set => SetProperty(ref _selectedExportFormat, value); }
    private string _dataDirectory = string.Empty;
    public string DataDirectory { get => _dataDirectory; set => SetDirty(ref _dataDirectory, value); }
    private ProviderProfile? _selectedProviderProfile;
    private string _activeProviderProfileId = string.Empty;
    /// <summary>设置草稿中明确标记为“当前使用”的配置。</summary>
    public string ActiveProviderProfileId => _activeProviderProfileId;
    public ProviderProfile? SelectedProviderProfile
    {
        get => _selectedProviderProfile;
        set
        {
            if (_selectedProviderProfile == value) return;
            FlushModelMapping(); // 切换前把当前映射编辑写回旧配置
            if (SetProperty(ref _selectedProviderProfile, value))
            {
                _apiKey = value is not null && _pendingApiKeys.TryGetValue(value.SecretId, out var pending) && !string.IsNullOrEmpty(pending)
                    ? pending
                    : string.Empty;
                OnPropertyChanged(nameof(ApiKey));
                OnPropertyChanged(nameof(HasStoredApiKey));
                OnPropertyChanged(nameof(ApiKeyStateText));
                OnPropertyChanged(nameof(ApiKeyStatusKind));
                OnPropertyChanged(nameof(SelectedProviderPlatform));
                OnPropertyChanged(nameof(IsApiBaseEditable));
                OnPropertyChanged(nameof(AvailableModels));
                OnPropertyChanged(nameof(SelectedModel));
                OnPropertyChanged(nameof(SelectedProviderHelp));
                OnPropertyChanged(nameof(SelectedProviderWebsite));
                OnPropertyChanged(nameof(SelectedProviderApiKeyUrl));
                OnPropertyChanged(nameof(ModelId));
                OnPropertyChanged(nameof(ApiBaseInput));
                OnPropertyChanged(nameof(ShowLegacyModelMapping));
                OnPropertyChanged(nameof(ProviderVerificationText));
                OnPropertyChanged(nameof(CanDuplicateOrEditProvider));
                OnPropertyChanged(nameof(CanRemoveProvider));
                OnPropertyChanged(nameof(IsSelectedProviderActive));
                ReloadModelMapping();
                OnPropertyChanged(nameof(CanTestConnection));
                ResetConnectionVerification();
            }
        }
    }
    public bool IsSelectedProviderActive => SelectedProviderProfile is not null &&
        string.Equals(SelectedProviderProfile.Id, _activeProviderProfileId, StringComparison.Ordinal);

    [RelayCommand]
    private void SetActiveProvider(ProviderProfile? profile)
    {
        if (profile is null || !ProviderProfiles.Contains(profile)) return;
        _activeProviderProfileId = profile.Id;
        SelectedProviderProfile = profile;
        HasChanges = true;
        OnPropertyChanged(nameof(ActiveProviderProfileId));
        OnPropertyChanged(nameof(IsSelectedProviderActive));
        OnPropertyChanged(nameof(FilteredProviderProfiles));
    }
    public ProviderPlatformOption? SelectedProviderPlatform
    {
        get => SelectedProviderProfile is null ? null : ProviderPlatformCatalog.Get(SelectedProviderProfile.Platform);
        set
        {
            if (SelectedProviderProfile is null || value is null || SelectedProviderProfile.Platform == value.Platform) return;
            _pendingApiKeys[SelectedProviderProfile.SecretId] = null;
            _apiKey = string.Empty;
            _providerConnectionChanged = true;
            ResetConnectionVerification();
            ProviderPlatformCatalog.ApplyPreset(SelectedProviderProfile, value.Platform);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsApiBaseEditable));
            OnPropertyChanged(nameof(SelectedProviderProfile));
            OnPropertyChanged(nameof(AvailableModels));
            OnPropertyChanged(nameof(SelectedModel));
            OnPropertyChanged(nameof(SelectedProviderHelp));
            OnPropertyChanged(nameof(SelectedProviderWebsite));
            OnPropertyChanged(nameof(SelectedProviderApiKeyUrl));
            OnPropertyChanged(nameof(ApiKey));
            OnPropertyChanged(nameof(HasStoredApiKey));
            OnPropertyChanged(nameof(ApiKeyStateText));
            OnPropertyChanged(nameof(ApiKeyStatusKind));
            OnPropertyChanged(nameof(ModelId));
            OnPropertyChanged(nameof(ApiBaseInput));
            OnPropertyChanged(nameof(ShowLegacyModelMapping));
            OnPropertyChanged(nameof(ProviderVerificationText));
            HasChanges = true;
            ConnectionStatus = $"已切换到 {value.DisplayName}，请填写 API Key 并测试连接";
            ConnectionStatusKind = ConnectionStatusKind.Neutral;
        }
    }
    public bool IsApiBaseEditable => SelectedProviderProfile?.Platform == ProviderPlatform.CustomOpenAICompatible;

    public string ModelId
    {
        get => SelectedProviderProfile?.Model ?? string.Empty;
        set
        {
            if (SelectedProviderProfile is null || SelectedProviderProfile.Model == value) return;
            SelectedProviderProfile.Model = value;
            HasChanges = true;
            _providerConnectionChanged = true;
            ResetConnectionVerification();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedModel));
        }
    }

    public string ApiBaseInput
    {
        get => SelectedProviderProfile?.ApiBase ?? string.Empty;
        set
        {
            if (SelectedProviderProfile is null || SelectedProviderProfile.ApiBase == value) return;
            SelectedProviderProfile.ApiBase = value;
            HasChanges = true;
            _providerConnectionChanged = true;
            ResetConnectionVerification();
            OnPropertyChanged();
        }
    }

    public bool ShowLegacyModelMapping => SelectedProviderProfile?.EnableModelMapping == true;
    public string ProviderVerificationText => SelectedProviderPlatform is { } option
        ? $"预设校验于 {option.VerifiedOn} · 模型 ID 可直接修改"
        : string.Empty;

    /// <summary>当前供应商可选模型列表（显示名称 → 实际 Model ID）。</summary>
    public IReadOnlyList<ModelDefinition> AvailableModels
    {
        get
        {
            var option = SelectedProviderPlatform;
            if (option is null) return [];
            if (option.Models is { Count: > 0 } models) return models;
            return string.IsNullOrWhiteSpace(option.DefaultModel) ? [] : [new ModelDefinition(option.DefaultModel, option.DefaultModel)];
        }
    }

    /// <summary>当前选中的模型：显示名称列表映射到 ProviderProfile.Model 的实际 Model ID。</summary>
    public ModelDefinition? SelectedModel
    {
        get
        {
            if (SelectedProviderProfile is null || string.IsNullOrWhiteSpace(SelectedProviderProfile.Model)) return null;
            return AvailableModels.FirstOrDefault(model => model.ModelId == SelectedProviderProfile.Model)
                ?? new ModelDefinition(SelectedProviderProfile.Model, SelectedProviderProfile.Model);
        }
        set
        {
            if (SelectedProviderProfile is null || value is null || SelectedProviderProfile.Model == value.ModelId) return;
            SelectedProviderProfile.Model = value.ModelId;
            _providerConnectionChanged = true;
            ResetConnectionVerification();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedProviderProfile));
            HasChanges = true;
        }
    }

    public string SelectedProviderHelp => SelectedProviderPlatform?.HelpText ?? string.Empty;
    public string SelectedProviderWebsite => SelectedProviderPlatform?.WebsiteUrl ?? string.Empty;
    public string SelectedProviderApiKeyUrl => SelectedProviderPlatform?.ApiKeyUrl ?? string.Empty;

    /// <summary>模型映射编辑集合：统一模型名 → 实际 Model ID。</summary>
    public ObservableCollection<ModelMappingEntry> ModelMappingEntries { get; } = [];

    /// <summary>映射摘要：如「已配置 3 条映射」/「未启用」。</summary>
    public string MappingSummary => EnableModelMapping
        ? $"已配置 {ModelMappingEntries.Count} 条映射"
        : "未启用";

    /// <summary>启用模型映射：开启后 Model 视为统一名，经 ModelMapping 解析为实际 Model ID。</summary>
    public bool EnableModelMapping
    {
        get => SelectedProviderProfile?.EnableModelMapping ?? false;
        set
        {
            if (SelectedProviderProfile is null || SelectedProviderProfile.EnableModelMapping == value) return;
            SelectedProviderProfile.EnableModelMapping = value;
            HasChanges = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MappingSummary));
            OnPropertyChanged(nameof(ShowLegacyModelMapping));
        }
    }

    [RelayCommand]
    private void AddModelMappingEntry()
    {
        ModelMappingEntries.Add(new ModelMappingEntry());
        HasChanges = true;
        OnPropertyChanged(nameof(MappingSummary));
    }

    [RelayCommand]
    private void RemoveModelMappingEntry(ModelMappingEntry? entry)
    {
        if (entry is not null && ModelMappingEntries.Remove(entry))
        {
            HasChanges = true;
            OnPropertyChanged(nameof(MappingSummary));
        }
    }

    /// <summary>从当前配置的字典重建映射条目（切换配置/加载时调用）。</summary>
    private void ReloadModelMapping()
    {
        ModelMappingEntries.Clear();
        if (SelectedProviderProfile is { ModelMapping: { } map })
        {
            foreach (var pair in map)
            {
                ModelMappingEntries.Add(new ModelMappingEntry { Key = pair.Key, Value = pair.Value });
            }
        }
        OnPropertyChanged(nameof(EnableModelMapping));
    }

    /// <summary>把编辑条目写回当前配置的字典（保存前调用），跳过空 Key。</summary>
    private void FlushModelMapping()
    {
        if (SelectedProviderProfile is null) return;
        SelectedProviderProfile.ModelMapping.Clear();
        foreach (var entry in ModelMappingEntries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key)) continue;
            SelectedProviderProfile.ModelMapping[entry.Key.Trim()] = entry.Value;
        }
    }
    private string _apiKey = string.Empty;
    public string ApiKey
    {
        get => _apiKey;
        set
        {
            if (!SetProperty(ref _apiKey, value)) return;
            if (SelectedProviderProfile is not null)
            {
                if (string.IsNullOrEmpty(value))
                {
                    if (_pendingApiKeys.TryGetValue(SelectedProviderProfile.SecretId, out var pending) && pending is not null)
                        _pendingApiKeys.Remove(SelectedProviderProfile.SecretId);
                }
                else
                {
                    _pendingApiKeys[SelectedProviderProfile.SecretId] = value;
                }
            }
            HasChanges = true;
            _providerConnectionChanged = true;
            ResetConnectionVerification();
            OnPropertyChanged(nameof(HasStoredApiKey));
            OnPropertyChanged(nameof(ApiKeyStateText));
            OnPropertyChanged(nameof(ApiKeyStatusKind));
        }
    }

    public bool HasStoredApiKey
    {
        get
        {
            if (SelectedProviderProfile is null) return false;
            if (SelectedProviderPlatform?.RequiresApiKey == false) return true;
            if (_pendingApiKeys.TryGetValue(SelectedProviderProfile.SecretId, out var pending)) return !string.IsNullOrWhiteSpace(pending);
            return _secretStore.Exists(SelectedProviderProfile.SecretId);
        }
    }

    public string ApiKeyStateText
    {
        get
        {
            if (SelectedProviderProfile is null) return "未配置";
            if (SelectedProviderPlatform?.RequiresApiKey == false) return "本地模型无需密钥";
            if (_pendingApiKeys.TryGetValue(SelectedProviderProfile.SecretId, out var pending))
                return pending is null ? "保存后移除" : "待保存的新密钥";
            return _secretStore.Exists(SelectedProviderProfile.SecretId) ? "已安全保存" : "未配置";
        }
    }
    private string _connectionStatus = "尚未测试";
    public string ConnectionStatus { get => _connectionStatus; private set => SetProperty(ref _connectionStatus, value); }

    private string _connectionDiagnostic = string.Empty;
    public string ConnectionDiagnostic { get => _connectionDiagnostic; private set => SetProperty(ref _connectionDiagnostic, value); }

    private bool _isTestingConnection;
    public bool IsTestingConnection
    {
        get => _isTestingConnection;
        private set
        {
            if (!SetProperty(ref _isTestingConnection, value)) return;
            OnPropertyChanged(nameof(CanTestConnection));
        }
    }
    public bool CanTestConnection => !IsTestingConnection && SelectedProviderProfile is not null;

    private bool _canSaveWithoutVerification;
    public bool CanSaveWithoutVerification { get => _canSaveWithoutVerification; private set => SetProperty(ref _canSaveWithoutVerification, value); }

    private bool _providerConnectionChanged;
    private string _verifiedConnectionFingerprint = string.Empty;
    private int _connectionTestVersion;

    private void ResetConnectionVerification()
    {
        _connectionTestVersion++;
        _verifiedConnectionFingerprint = string.Empty;
        ConnectionStatus = "待测试";
        ConnectionStatusKind = ConnectionStatusKind.Neutral;
        CanSaveWithoutVerification = false;
        ConnectionDiagnostic = string.Empty;
    }

    private string BuildConnectionFingerprint()
    {
        var profile = SelectedProviderProfile;
        if (profile is null) return string.Empty;
        var key = ResolveSelectedApiKey() ?? string.Empty;
        // 仅用于本次进程内比较，不写入日志、配置或诊断文本。
        return string.Join("\u001f", profile.Id, profile.Platform, profile.Protocol,
            profile.ApiBase.Trim(), profile.Model.Trim(), key);
    }

    private ConnectionStatusKind _connectionStatusKind = ConnectionStatusKind.Neutral;
    public ConnectionStatusKind ConnectionStatusKind
    {
        get => _connectionStatusKind;
        private set => SetProperty(ref _connectionStatusKind, value);
    }
    private string _updateCheckUrl = string.Empty;
    public string UpdateCheckUrl { get => _updateCheckUrl; set => SetDirty(ref _updateCheckUrl, value); }
    private bool _autoCheckUpdates = true;
    public bool AutoCheckUpdates { get => _autoCheckUpdates; set => SetDirty(ref _autoCheckUpdates, value); }
    private string _healthSummary = "尚未执行健康检查";
    public string HealthSummary { get => _healthSummary; private set => SetProperty(ref _healthSummary, value); }
    private readonly LegacyKnowledgeDataService _legacyKnowledge = new(App.DataRoot);
    public bool LegacyKnowledgeDetected => _legacyKnowledge.Exists;
    public string LegacyKnowledgeStatus => LegacyKnowledgeDetected
        ? "检测到旧版知识数据。Vesper 不会读取或注入这些内容；你可以导出备份或明确永久删除。"
        : string.Empty;

    public void ExportLegacyKnowledge(string destination)
    {
        _legacyKnowledge.Export(destination);
        DataStatus = "旧版知识数据已导出。原文件保持不变。";
    }

    public void DeleteLegacyKnowledge()
    {
        _legacyKnowledge.DeletePermanently();
        OnPropertyChanged(nameof(LegacyKnowledgeDetected));
        OnPropertyChanged(nameof(LegacyKnowledgeStatus));
        DataStatus = "旧版知识数据已永久删除。";
    }

    private AgentSkillPackageService? _agentSkillPackages;
    private bool _strategiesLoaded;
    private Task? _strategiesLoadTask;
    public ObservableCollection<AgentSkillRecord> AgentSkillItems { get; } = [];
    [ObservableProperty] private AgentSkillRecord? _selectedAgentSkill;
    [ObservableProperty] private AgentSkillCandidate? _pendingAgentSkill;
    [ObservableProperty] private string _skillEditorText = string.Empty;
    [ObservableProperty] private bool _isSkillEditorOpen;
    [ObservableProperty] private string _strategyStatus = "内置策略始终可用；外部 Skill 仅作为受限表达方法运行。";
    [ObservableProperty] private bool _isStrategiesLoading;
    private bool _strategiesLoadFailed;
    public bool IsStrategiesLoadFailed => _strategiesLoadFailed;
    public Task StrategiesLoadTask => _strategiesLoadTask ?? Task.CompletedTask;
    public bool HasSelectedAgentSkill => SelectedAgentSkill is not null;
    public bool IsSkillDetailsVisible => HasSelectedAgentSkill && !IsSkillEditorOpen;
    public bool HasPendingAgentSkill => PendingAgentSkill is not null;
    partial void OnSelectedAgentSkillChanged(AgentSkillRecord? value)
    {
        OnPropertyChanged(nameof(HasSelectedAgentSkill));
        OnPropertyChanged(nameof(IsSkillDetailsVisible));
    }
    partial void OnIsSkillEditorOpenChanged(bool value) => OnPropertyChanged(nameof(IsSkillDetailsVisible));
    partial void OnPendingAgentSkillChanged(AgentSkillCandidate? value) => OnPropertyChanged(nameof(HasPendingAgentSkill));

    public SettingsViewModel(
        ArchiveService? archiveService = null,
        ISecretStore? secretStore = null,
        Func<ProviderProfile, string?, CancellationToken, Task<ConnectionTestResult>>? connectionTester = null,
        Func<IReadOnlyList<AgentSkillRecord>>? skillCatalogLoader = null)
    {
        _archiveService = archiveService ?? App.ArchiveService;
        _secretStore = secretStore ?? App.SecretStore;
        _connectionTester = connectionTester ?? TestConnectionWithServiceAsync;
        _skillCatalogLoader = skillCatalogLoader;
        ProviderPlatformView = new ListCollectionView(ProviderPlatformCatalog.Options.ToList());
        ProviderPlatformView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProviderPlatformOption.TierDisplayName)));
        _originalDisplaySettings = App.Settings.Clone();
        foreach (var skin in App.SkinService.AvailableSkins.Where(skin => !skin.IsBuiltIn))
            SpecialSkins.Add(new SettingOption(skin.Id, skin.DisplayName));
        LoadFromSettings();
        LoadArchive();
        LoadHealthReport(App.LatestHealthReport);
    }

    partial void OnSelectedSectionChanged(string value)
    {
        // “专业能力”管理统一从“表达能力”概览的“管理能力”入口进入，避免重复入口。
        if (value == "表达能力") ExpressionAbilityPane = ExpressionAbilityPane.Overview;
        else if (ExpressionAbilityPane != ExpressionAbilityPane.Overview)
            ExpressionAbilityPane = ExpressionAbilityPane.Overview;
    }

    partial void OnExpressionAbilityPaneChanged(ExpressionAbilityPane value)
    {
        OnPropertyChanged(nameof(IsExpressionOverview));
        OnPropertyChanged(nameof(IsExpressionSkills));
        if (value == ExpressionAbilityPane.Skills) _ = EnsureStrategiesLoadedAsync();
    }

    public bool IsExpressionOverview => ExpressionAbilityPane == ExpressionAbilityPane.Overview;
    public bool IsExpressionSkills => ExpressionAbilityPane == ExpressionAbilityPane.Skills;

    [RelayCommand]
    private void OpenExpressionOverview() => ExpressionAbilityPane = ExpressionAbilityPane.Overview;

    [RelayCommand]
    private void OpenExpressionSkills() => ExpressionAbilityPane = ExpressionAbilityPane.Skills;

    [RelayCommand]
    private void RetryExpressionSkillsLoad()
    {
        _strategiesLoaded = false;
        _strategiesLoadFailed = false;
        OnPropertyChanged(nameof(IsStrategiesLoadFailed));
        _strategiesLoadTask = null;
        _ = EnsureStrategiesLoadedAsync();
    }

    public async Task InspectAgentSkillAsync(string sourcePath)
    {
        try
        {
            await EnsureStrategiesLoadedAsync();
            if (!_strategiesLoaded || _agentSkillPackages is null) return;
            IsStrategiesLoading = true;
            PendingAgentSkill = (await Task.Run(() => _agentSkillPackages.Inspect(sourcePath))).FirstOrDefault();
            if (PendingAgentSkill is null) { StrategyStatus = "未找到可识别的 SKILL.md。"; return; }
            if (PendingAgentSkill.Status is SkillCompatibilityStatus.RequiresTools or SkillCompatibilityStatus.ReviewRequired)
            {
                _agentSkillPackages.PreserveForReview(PendingAgentSkill);
            }
            StrategyStatus = PendingAgentSkill.Status switch
            {
                SkillCompatibilityStatus.Ready => "检查通过。确认后可导入。",
                SkillCompatibilityStatus.NeedsMapping => "格式兼容，请选择用于表达润色还是提示词优化。",
                SkillCompatibilityStatus.RequiresTools => "此 Skill 依赖外部工具，已隔离保存并禁止启用。",
                SkillCompatibilityStatus.ReviewRequired => "发现未知资源，已隔离保存等待人工复核，当前禁止启用。",
                _ => "Skill 格式无效，不能安装。"
            };
        }
        catch (Exception exception) { StrategyStatus = exception.Message; }
        finally { IsStrategiesLoading = false; }
    }

    [RelayCommand]
    private void InstallAgentSkill(string? mode)
    {
        if (PendingAgentSkill is null || !PendingAgentSkill.CanEnable || _agentSkillPackages is null) return;
        var selectedMode = string.Equals(mode, "prompt", StringComparison.OrdinalIgnoreCase)
            ? ApplicationMode.PromptOptimize : ApplicationMode.Polish;
        if (PendingAgentSkill.Status == SkillCompatibilityStatus.Ready && PendingAgentSkill.Modes.Count == 1)
            selectedMode = PendingAgentSkill.Modes[0];
        try
        {
            var installed = _agentSkillPackages.Install(PendingAgentSkill, selectedMode);
            StrategyStatus = $"已导入 {installed.Name}，用于{(selectedMode == ApplicationMode.Polish ? "表达润色" : "提示词优化")}。";
            PendingAgentSkill = null;
            ReloadStrategyItems();
        }
        catch (Exception exception) { StrategyStatus = exception.Message; }
    }

    public void ExportSelectedAgentSkill(string destinationZip)
    {
        if (SelectedAgentSkill is null) return;
        if (!EnsureStrategiesLoaded()) return;
        _agentSkillPackages!.Export(SelectedAgentSkill.Id, destinationZip);
        StrategyStatus = "已导出标准 Skill ZIP。";
    }

    [RelayCommand]
    private void ToggleSelectedAgentSkill()
    {
        if (SelectedAgentSkill is null) return;
        if (!EnsureStrategiesLoaded()) return;
        var enabled = !SelectedAgentSkill.IsEnabled;
        _agentSkillPackages!.SetEnabled(SelectedAgentSkill.Id, enabled);
        var selectedName = SelectedAgentSkill.Id;
        ReloadStrategyItems(selectedName);
        StrategyStatus = enabled ? "已启用，下一次处理立即生效。" : "已停用，不再加入模型请求。";
    }

    [RelayCommand]
    private void EditSelectedAgentSkill()
    {
        if (SelectedAgentSkill is null) return;
        if (!EnsureStrategiesLoaded()) return;
        if (!SelectedAgentSkill.CanEdit)
        {
            var baseName = SelectedAgentSkill.UpstreamName + "-custom";
            var cloneName = baseName;
            var index = 2;
            while (AgentSkillItems.Any(item => string.Equals(item.Name, cloneName, StringComparison.OrdinalIgnoreCase)))
                cloneName = baseName + "-" + index++;
            SelectedAgentSkill = _agentSkillPackages!.CloneForEditing(SelectedAgentSkill.Id, cloneName);
            ReloadStrategyItems(cloneName);
        }
        SkillEditorText = SelectedAgentSkill?.Instructions ?? string.Empty;
        IsSkillEditorOpen = true;
        StrategyStatus = "正在编辑自定义副本；默认 Skill 保持不变。";
    }

    [RelayCommand]
    private void SaveSkillEdits()
    {
        if (SelectedAgentSkill is null || !SelectedAgentSkill.CanEdit || !IsSkillEditorOpen) return;
        try
        {
            _agentSkillPackages!.SaveInstructions(SelectedAgentSkill.Id, SkillEditorText);
            var selectedName = SelectedAgentSkill.Id;
            IsSkillEditorOpen = false;
            ReloadStrategyItems(selectedName);
            StrategyStatus = "自定义 Skill 已保存并设为当前策略。";
        }
        catch (Exception exception) { StrategyStatus = exception.Message; }
    }

    [RelayCommand]
    private void CancelSkillEdits()
    {
        IsSkillEditorOpen = false;
        SkillEditorText = string.Empty;
    }

    [RelayCommand]
    private void DeleteSelectedAgentSkill()
    {
        if (SelectedAgentSkill is null || !SelectedAgentSkill.CanDelete) return;
        try
        {
            var name = SelectedAgentSkill.Id;
            _agentSkillPackages!.Remove(name);
            IsSkillEditorOpen = false;
            ReloadStrategyItems();
            StrategyStatus = "自定义 Skill 已删除。";
        }
        catch (Exception exception) { StrategyStatus = exception.Message; }
    }

    [RelayCommand]
    private void UseDefaultStrategies()
    {
        if (!EnsureStrategiesLoaded()) return;
        foreach (var item in AgentSkillItems.Where(item => item.Source == AgentSkillSource.Preset))
            _agentSkillPackages!.SetEnabled(item.Id, true);
        ReloadStrategyItems();
        StrategyStatus = "已启用随框架导入的默认 Skill。";
    }

    private Task EnsureStrategiesLoadedAsync()
    {
        if (_strategiesLoaded) return Task.CompletedTask;
        return _strategiesLoadTask ??= LoadStrategiesCoreAsync();
    }

    private bool EnsureStrategiesLoaded()
    {
        if (_strategiesLoaded) return true;
        _ = EnsureStrategiesLoadedAsync();
        StrategyStatus = "正在加载表达能力，请稍候…";
        return false;
    }

    private async Task LoadStrategiesCoreAsync()
    {
        IsStrategiesLoading = true;
        _agentSkillPackages = new AgentSkillPackageService(Path.Combine(App.DataRoot, "agent-skills"));
        try
        {
            var items = await Task.Run(_skillCatalogLoader ?? _agentSkillPackages.ListInstalled);
            AgentSkillItems.Clear();
            foreach (var item in items) AgentSkillItems.Add(item);
            SelectedAgentSkill = AgentSkillItems.FirstOrDefault();
            _strategiesLoaded = true;
            StrategyStatus = AgentSkillItems.Count == 0
                ? "尚未安装扩展能力；Vesper 默认表达仍可正常使用。"
                : "已加载表达能力。启停状态会在下一次处理时生效。";
        }
        catch (Exception exception)
        {
            var detail = string.IsNullOrWhiteSpace(exception.Message) ? "本地能力文件未能完整读取。" : exception.Message;
            StrategyStatus = AgentSkillItems.Count > 0
                ? $"已保留 {AgentSkillItems.Count} 项能力；Vesper 默认表达仍可用。"
                : $"能力暂不可用，Vesper 默认表达仍可用：{detail}";
            _strategiesLoadFailed = true;
            _strategiesLoaded = true;
            OnPropertyChanged(nameof(IsStrategiesLoadFailed));
        }
        finally { IsStrategiesLoading = false; }
    }

    private void ReloadStrategyItems(string? selectedName = null)
    {
        if (_agentSkillPackages is null) return;
        AgentSkillItems.Clear();
        foreach (var item in _agentSkillPackages.ListInstalled()) AgentSkillItems.Add(item);
        SelectedAgentSkill = AgentSkillItems.FirstOrDefault(item => item.Id == selectedName) ?? AgentSkillItems.FirstOrDefault();
    }

    public void MarkClean() => HasChanges = false;

    public bool TryDiscardChanges(Func<bool> confirmDiscard)
    {
        if (HasChanges && !confirmDiscard()) return false;
        CancelCommand.Execute(null);
        return true;
    }

    public void ValidationMessageForRecorder(string message) => ValidationMessage = message;

    public void RefreshSelectedApiKey()
    {
        _apiKey = string.Empty;
        OnPropertyChanged(nameof(ApiKey));
        OnPropertyChanged(nameof(HasStoredApiKey));
        OnPropertyChanged(nameof(ApiKeyStateText));
        OnPropertyChanged(nameof(ApiKeyStatusKind));
        MarkClean();
    }

    [RelayCommand]
    private void ClearApiKey()
    {
        if (SelectedProviderProfile is null) return;
        _pendingApiKeys[SelectedProviderProfile.SecretId] = null;
        _apiKey = string.Empty;
        HasChanges = true;
        _providerConnectionChanged = true;
        ResetConnectionVerification();
        OnPropertyChanged(nameof(ApiKey));
        OnPropertyChanged(nameof(HasStoredApiKey));
        OnPropertyChanged(nameof(ApiKeyStateText));
        OnPropertyChanged(nameof(ApiKeyStatusKind));
    }

    [RelayCommand]
    private void AddProvider()
    {
        var id = "profile-" + Guid.NewGuid().ToString("N")[..8];
        var profile = new ProviderProfile { Id = id, Name = "新模型", SecretId = "provider-" + id };
        ProviderProfiles.Add(profile);
        SelectedProviderProfile = profile;
        HasChanges = true;
        _providerConnectionChanged = true;
        OnPropertyChanged(nameof(FilteredProviderProfiles));
        OnPropertyChanged(nameof(CanRemoveProvider));
        NavigateToProviderEdit(profile);
    }

    [RelayCommand]
    private void DuplicateProvider()
    {
        if (SelectedProviderProfile is null) return;
        FlushModelMapping(); // 复制前把当前映射编辑写回，随 Clone 一并保留
        var duplicate = SelectedProviderProfile.Clone();
        duplicate.Id = "profile-" + Guid.NewGuid().ToString("N")[..8];
        duplicate.SecretId = "provider-" + duplicate.Id;
        duplicate.Name = string.IsNullOrWhiteSpace(duplicate.Name) ? "配置副本" : duplicate.Name + " 副本";
        ProviderProfiles.Add(duplicate);
        SelectedProviderProfile = duplicate;
        HasChanges = true;
        _providerConnectionChanged = true;
        OnPropertyChanged(nameof(FilteredProviderProfiles));
        OnPropertyChanged(nameof(CanRemoveProvider));
        NavigateToProviderEdit(duplicate);
    }

    [RelayCommand]
    private void NavigateToProviderList()
    {
        EditingProviderProfile = null;
        ProviderViewMode = ProviderProfileViewMode.List;
    }

    [RelayCommand]
    private void NavigateToProviderEdit(ProviderProfile? profile)
    {
        EditingProviderProfile = profile ?? SelectedProviderProfile;
        if (EditingProviderProfile is null) return;
        SelectedProviderProfile = EditingProviderProfile;
        OnPropertyChanged(nameof(ApiKeyStatusKind));
        ProviderViewMode = ProviderProfileViewMode.Edit;
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task TestConnectionAsync()
    {
        await TestSelectedConnectionAsync();
    }

    [RelayCommand]
    private void CopyConnectionDiagnostic()
    {
        if (!string.IsNullOrWhiteSpace(ConnectionDiagnostic))
            Clipboard.SetText(ConnectionDiagnostic);
    }

    private static async Task<ConnectionTestResult> TestConnectionWithServiceAsync(
        ProviderProfile profile, string? apiKey, CancellationToken cancellationToken)
    {
        using var service = new AIService(profile, apiKey);
        return await service.TestConnectionAsync(cancellationToken);
    }

    private string? ResolveSelectedApiKey()
    {
        if (SelectedProviderProfile is null) return null;
        if (_pendingApiKeys.TryGetValue(SelectedProviderProfile.SecretId, out var pending)) return pending;
        return _secretStore.Read(SelectedProviderProfile.SecretId);
    }

    private async Task<ConnectionTestResult?> TestSelectedConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedProviderProfile is null) return null;
        if (IsTestingConnection) return null;
        IsTestingConnection = true;
        var testVersion = ++_connectionTestVersion;
        var fingerprint = BuildConnectionFingerprint();
        ConnectionStatus = "正在验证连接…";
        ConnectionDiagnostic = string.Empty;
        ConnectionStatusKind = ConnectionStatusKind.Pending;
        CanSaveWithoutVerification = false;
        try
        {
            var result = await _connectionTester(SelectedProviderProfile.Clone(), ResolveSelectedApiKey(), cancellationToken);
            // 如果用户在等待期间切换了配置或修改了字段，旧结果不能覆盖新草稿。
            if (testVersion != _connectionTestVersion) return null;
            ConnectionStatus = result.UserMessage;
            ConnectionDiagnostic = result.DiagnosticSummary;
            ConnectionStatusKind = result.Status == ConnectionTestStatus.Success
                ? ConnectionStatusKind.Success
                : ConnectionStatusKind.Failure;
            CanSaveWithoutVerification = !result.CanSaveAsVerified;
            _verifiedConnectionFingerprint = result.CanSaveAsVerified ? fingerprint : string.Empty;
            return result;
        }
        catch (OperationCanceledException)
        {
            ConnectionStatus = "连接验证已取消。";
            ConnectionStatusKind = ConnectionStatusKind.Neutral;
            throw;
        }
        catch
        {
            ConnectionStatus = "连接验证失败，请检查网络后重试。";
            ConnectionStatusKind = ConnectionStatusKind.Failure;
            CanSaveWithoutVerification = true;
            return null;
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    public async Task<bool> TrySaveAsync(bool saveWithoutVerification = false, CancellationToken cancellationToken = default)
    {
        while (IsTestingConnection)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(40, cancellationToken);
        }
        if (_providerConnectionChanged && !saveWithoutVerification &&
            !string.Equals(_verifiedConnectionFingerprint, BuildConnectionFingerprint(), StringComparison.Ordinal))
        {
            var result = await TestSelectedConnectionAsync(cancellationToken);
            if (result?.CanSaveAsVerified != true) return false;
        }
        return TrySave();
    }

    /// <summary>
    /// 仅供设置页在用户明确确认后保存未验证草稿；不作为自动提交的回退路径。
    /// </summary>
    public Task<bool> SaveDraftAfterUserConfirmationAsync(CancellationToken cancellationToken = default) =>
        TrySaveAsync(saveWithoutVerification: true, cancellationToken);

    [RelayCommand]
    private void RemoveProvider()
    {
        if (SelectedProviderProfile is null || ProviderProfiles.Count <= 1) return;
        _removedSecretIds.Add(SelectedProviderProfile.SecretId);
        _pendingApiKeys.Remove(SelectedProviderProfile.SecretId);
        var index = ProviderProfiles.IndexOf(SelectedProviderProfile);
        var wasActive = string.Equals(_activeProviderProfileId, SelectedProviderProfile.Id, StringComparison.Ordinal);
        ProviderProfiles.Remove(SelectedProviderProfile);
        SelectedProviderProfile = ProviderProfiles[Math.Max(0, index - 1)];
        if (wasActive && SelectedProviderProfile is not null)
        {
            _activeProviderProfileId = SelectedProviderProfile.Id;
            OnPropertyChanged(nameof(ActiveProviderProfileId));
            OnPropertyChanged(nameof(IsSelectedProviderActive));
        }
        HasChanges = true;
        OnPropertyChanged(nameof(FilteredProviderProfiles));
        OnPropertyChanged(nameof(CanRemoveProvider));
        if (ProviderViewMode == ProviderProfileViewMode.Edit)
            NavigateToProviderList();
    }

    [RelayCommand]
    private void AddOptimizationPreset()
    {
        var preset = new OptimizationPreset
        {
            Name = "新预设",
            Mode = DefaultMode,
            Category = DefaultCategory,
            Depth = DefaultDepth,
            OutputStyle = OutputStyle,
            Instructions = CustomStyleInstructions,
            CustomSystemPrompt = CustomSystemPrompt
        };
        OptimizationPresets.Add(preset);
        SelectedOptimizationPreset = preset;
        HasChanges = true;
    }

    [RelayCommand]
    private void RemoveOptimizationPreset()
    {
        if (SelectedOptimizationPreset is null) return;
        var index = OptimizationPresets.IndexOf(SelectedOptimizationPreset);
        OptimizationPresets.Remove(SelectedOptimizationPreset);
        SelectedOptimizationPreset = OptimizationPresets.Count == 0
            ? null
            : OptimizationPresets[Math.Max(0, index - 1)];
        HasChanges = true;
    }

    [RelayCommand]
    private void ResetDisplayDefaults()
    {
        SkinId = "default";
        ThemeMode = "System";
        EditorFontSize = 13;
        UiScale = 1;
        EditorDefaultHeight = 36;
        WindowOpacity = 1;
        AnimationsEnabled = true;
        CompanionDriverMode = CompanionDriverMode.Local;
        ShowDiff = true;
    }

    [RelayCommand]
    private void ResetAllHotkeys()
    {
        Hotkey = "Ctrl+Shift+H";
        QuickPolishHotkey = string.Empty;
        QuickPromptHotkey = string.Empty;
        CopyResultHotkey = string.Empty;
    }

    [RelayCommand]
    private void LoadArchive()
    {
        if (!TryParseArchiveDates(out var from, out var to)) return;
        ArchiveItems.Clear();
        foreach (var revision in _archiveService.Search(ArchiveSearch, ShowDeleted, ArchiveModeFilter.Mode, from, to)) ArchiveItems.Add(revision);
        RefreshDataStatus();
    }

    [RelayCommand]
    private void DeleteArchive()
    {
        if (SelectedArchiveItem is null) return;
        _archiveService.SoftDeleteRevision(SelectedArchiveItem.Id, DateTimeOffset.Now);
        LoadArchive();
    }

    [RelayCommand]
    private void RestoreArchive()
    {
        if (SelectedArchiveItem is null) return;
        _archiveService.RestoreRevision(SelectedArchiveItem.Id);
        LoadArchive();
    }

    [RelayCommand]
    private void SoftDeleteArchive()
    {
        DeleteArchive();
    }

    [RelayCommand]
    private void ToggleFavorite()
    {
        if (SelectedArchiveItem is null) return;
        _archiveService.SetFavorite(SelectedArchiveItem.Id, !SelectedArchiveItem.IsFavorite);
        LoadArchive();
        DataStatus = "收藏状态已更新。";
    }

    [RelayCommand]
    private void ToggleArchived()
    {
        if (SelectedArchiveItem is null) return;
        _archiveService.SetArchived(SelectedArchiveItem.Id, !SelectedArchiveItem.IsArchived);
        LoadArchive();
        DataStatus = "归档状态已更新。";
    }

    [RelayCommand]
    private void ExportArchive()
    {
        if (SelectedArchiveItem is null) return;
        if (string.IsNullOrWhiteSpace(ArchiveExportDirectory)) ArchiveExportDirectory = System.IO.Path.Combine(App.DataRoot, "exports");
        var path = _archiveService.ExportRevision(SelectedArchiveItem.Id, ArchiveExportDirectory, SelectedExportFormat);
        DataStatus = $"已导出到 {path}";
    }

    [RelayCommand]
    private void ExportAll()
    {
        if (!TryParseArchiveDates(out var from, out var to)) return;
        var records = _archiveService.Search(ArchiveSearch, ShowDeleted, ArchiveModeFilter.Mode, from, to);
        var directory = Path.Combine(App.DataRoot, "exports", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        foreach (var revision in records) _archiveService.ExportRevision(revision.Id, directory, SelectedExportFormat);
        DataStatus = $"已导出 {records.Count} 条记录到 {directory}";
    }

    [RelayCommand]
    private void BackupData()
    {
        var path = Path.Combine(App.DataRoot, "backups", $"Vesper-data-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        _dataManagement.CreateBackup(App.DataRoot, path, ConfigService.ConfigPath);
        DataStatus = $"备份已创建：{path}";
    }

    public void ImportRecord(string path)
    {
        var revision = _dataManagement.ImportRecord(path, _archiveService);
        LoadArchive();
        DataStatus = $"已导入：{revision.Topic}";
    }

    public void RestoreBackup(string path)
    {
        _dataManagement.RestoreBackup(path, App.DataRoot);
        LoadArchive();
        DataStatus = "备份已恢复；请重新启动 Vesper 以重新载入资料库。";
    }

    public void MigrateData(string destination)
    {
        _dataManagement.Migrate(App.DataRoot, destination);
        DataDirectory = Path.GetFullPath(destination);
        DataStatus = "迁移副本已校验。保存设置并重启后启用新目录；旧目录保留用于回滚。";
    }

    [RelayCommand]
    private void PermanentlyClearArchive()
    {
        _archiveService.PermanentlyDeleteAll();
        App.Settings.History.Clear();
        App.WorkspaceDraftService.Clear();
        LoadArchive();
        DataStatus = "资料库已清空";
    }

    [RelayCommand]
    private void ClearCache()
    {
        var removed = _dataManagement.ClearCache(App.DataRoot);
        DataStatus = $"缓存清理完成，共移除 {removed} 个文件；历史与配置未受影响。";
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task RunHealthCheckAsync()
    {
        HealthSummary = "正在检查 Local / External…";
        if (System.Windows.Application.Current is not App app)
        {
            HealthSummary = "健康检查仅可在 Vesper 应用内运行。";
            return;
        }
        LoadHealthReport(await app.RunHealthChecksAsync());
    }

    [RelayCommand]
    private void Save() => TrySave();

    public bool TrySave()
    {
        ValidationMessage = string.Empty;
        if (!DataDirectoryPolicy.TryResolve(DataDirectory, App.DefaultDataRoot, verifyWritable: true, out var normalizedDataDirectory, out var dataDirectoryError))
        {
            ValidationMessage = dataDirectoryError;
            return false;
        }
        foreach (var profile in ProviderProfiles)
        {
            if (profile.Type == ProviderType.Cloud &&
                Uri.TryCreate(profile.ApiBase, UriKind.Absolute, out var cloudEndpoint) &&
                cloudEndpoint.Scheme != Uri.UriSchemeHttps)
            {
                ValidationMessage = $"{profile.Name}：云端模型必须使用 HTTPS 地址，不能通过明文 HTTP 发送 API Key。";
                return false;
            }
            if (profile.Protocol == ProviderProtocol.OpenAICompatible &&
                Uri.TryCreate(profile.ApiBase, UriKind.Absolute, out var compatibleEndpoint) &&
                compatibleEndpoint.Scheme == Uri.UriSchemeHttp &&
                !compatibleEndpoint.IsLoopback)
            {
                ValidationMessage = $"{profile.Name}：OpenAI 兼容接口使用远程 HTTP 地址不安全，请改用 HTTPS 或仅限本地 localhost 测试。";
                return false;
            }
            if (!double.IsFinite(profile.Temperature) || profile.Temperature is < 0 or > 2)
            {
                ValidationMessage = $"{profile.Name}：Temperature 必须在 0–2 之间。";
                return false;
            }
            if (!double.IsFinite(profile.TopP) || profile.TopP is < 0 or > 1)
            {
                ValidationMessage = $"{profile.Name}：Top P 必须在 0–1 之间。";
                return false;
            }
            if (profile.MaxTokens is < 128 or > 32768)
            {
                ValidationMessage = $"{profile.Name}：Max Tokens 必须在 128–32768 之间。";
                return false;
            }
        }
        if (!string.IsNullOrWhiteSpace(UpdateCheckUrl) &&
            (!Uri.TryCreate(UpdateCheckUrl.Trim(), UriKind.Absolute, out var updateUri) ||
             updateUri.Scheme != Uri.UriSchemeHttps))
        {
            ValidationMessage = "自动更新地址必须使用 HTTPS 协议（留空可禁用自动更新）。";
            return false;
        }
        var normalizedHotkey = Hotkey.Trim();
        var candidateBindings = new Dictionary<GlobalHotkeyAction, string>
        {
            [GlobalHotkeyAction.ToggleWindow] = normalizedHotkey,
            [GlobalHotkeyAction.QuickPolish] = QuickPolishHotkey.Trim(),
            [GlobalHotkeyAction.QuickPromptOptimize] = QuickPromptHotkey.Trim(),
            [GlobalHotkeyAction.CopyResult] = CopyResultHotkey.Trim()
        };
        var hotkeyValidation = HotkeyBindingSet.Validate(candidateBindings);
        if (!hotkeyValidation.IsValid || string.IsNullOrWhiteSpace(normalizedHotkey))
        {
            ValidationMessage = string.IsNullOrWhiteSpace(normalizedHotkey)
                ? "呼出快捷键不能为空。"
                : hotkeyValidation.ErrorMessage;
            return false;
        }

        var previousSettings = App.Settings;
        var previousDataRoot = App.DataRoot;
        var settings = previousSettings.Clone();
        settings.DefaultCategory = DefaultCategory.GetDisplayName();
        settings.DefaultDepth = DefaultDepth.GetDisplayName();
        settings.EnabledPromptCategories = PromptCategoryOptions.Where(option => option.Enabled).Select(option => option.Category).ToList();
        settings.PromptHistoryLimit = PromptHistoryLimit;
        settings.NormalizePromptSettings();
        settings.Hotkey = normalizedHotkey;
        settings.QuickPolishHotkey = QuickPolishHotkey.Trim();
        settings.QuickPromptHotkey = QuickPromptHotkey.Trim();
        settings.CopyResultHotkey = CopyResultHotkey.Trim();
        settings.DefaultMode = DefaultMode;
        settings.EnabledModes = [ApplicationMode.Polish, ApplicationMode.PromptOptimize];
        settings.NormalizeProductModes();
        settings.ClipboardAutoRead = ClipboardAutoRead;
        settings.StartWithWindows = StartWithWindows;
        settings.FloatingBallEnabled = FloatingBallEnabled;
        settings.TrayEnabled = TrayEnabled;
        settings.AlwaysOnTop = AlwaysOnTop;
        settings.AutoArchive = AutoArchive;
        settings.HistoryEnabled = HistoryEnabled;
        settings.HistoryRetentionDays = HistoryRetentionDays;
        settings.SaveOriginalText = SaveOriginalText;
        settings.SaveOptimizedText = SaveOptimizedText;
        settings.IncognitoMode = IncognitoMode;
        settings.AutoCopyAfterOptimize = AutoCopyAfterOptimize;
        settings.EnterToSend = EnterToSend;
        settings.AutosaveDelayMilliseconds = AutosaveDelayMilliseconds;
        settings.ClarificationEnabled = ClarificationEnabled;
        settings.ShowDiff = ShowDiff;
        settings.DefaultPolishScenario = DefaultPolishScenario;
        settings.UserPersona = Persona.Trim();
        settings.OutputStyle = OutputStyle;
        settings.CustomStyleInstructions = CustomStyleInstructions.Trim();
        settings.CustomSystemPrompt = CustomSystemPrompt.Trim();
        settings.PreferenceLearningEnabled = PreferenceLearningEnabled;
        settings.PreserveMeaning = PreserveMeaning;
        settings.MinimalRewrite = MinimalRewrite;
        settings.ProfessionalTone = ProfessionalTone;
        settings.OptimizationPresets = OptimizationPresets.Select(preset => preset.Clone()).ToList();
        settings.ActivePresetId = SelectedOptimizationPreset?.Id ?? string.Empty;
        settings.UpdateCheckUrl = UpdateCheckUrl.Trim();
        settings.AutoCheckUpdates = AutoCheckUpdates;
        settings.DataDirectory = string.IsNullOrWhiteSpace(DataDirectory) ||
            string.Equals(normalizedDataDirectory, App.DefaultDataRoot, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : normalizedDataDirectory;
        settings.ThemeMode = ThemeMode;
        settings.SkinId = SkinId;
        settings.EditorFontSize = EditorFontSize;
        settings.UiScale = UiScale;
        settings.EditorDefaultHeight = EditorDefaultHeight;
        settings.AnimationsEnabled = AnimationsEnabled;
        settings.CompanionDriverMode = CompanionDriverMode;
        settings.WindowOpacity = WindowOpacity;
        settings.FloatingBallOpacity = FloatingBallOpacity;
        settings.FloatingBallSize = FloatingBallSize;
        settings.CloseBehavior = CloseBehavior;
        settings.EscapeBehavior = EscapeBehavior;
        settings.RememberWindowSize = RememberWindowSize;
        settings.RememberFloatingBallPosition = RememberFloatingBallPosition;
        settings.SnapFloatingBallToEdge = SnapFloatingBallToEdge;
        settings.ShowInTaskbar = ShowInTaskbar;
        FlushModelMapping(); // 把映射编辑条目写回当前配置，随 Clone 一并保存
        settings.ProviderProfiles = ProviderProfiles.Select(profile => profile.Clone()).ToList();
        settings.ActiveProviderProfileId = ProviderProfiles.Any(profile => profile.Id == _activeProviderProfileId)
            ? _activeProviderProfileId
            : (SelectedProviderProfile?.Id ?? ProviderProfiles[0].Id);
        settings.NormalizeProviderProfiles();
        settings.NormalizePromptSettings();
        settings.NormalizeResidentEntrypoints();
        settings.NormalizeDisplaySettings();

        try
        {
            App.HotkeyService.Stop();
            var registration = App.HotkeyService.SetHotkeys(candidateBindings);
            if (System.Windows.Application.Current is App app) app.RecordHotkeyRegistration(registration);
            if (!registration.AllSucceeded) ValidationMessage = "设置已保存，但部分全局快捷键不可用：" + registration.Summary;
        }
        catch (Exception exception)
        {
            try
            {
                var restored = App.HotkeyService.SetHotkeys(App.BuildHotkeyBindings(previousSettings));
                if (System.Windows.Application.Current is App app) app.RecordHotkeyRegistration(restored);
            }
            catch { }
            ValidationMessage = exception.Message;
            return false;
        }

        var active = settings.GetActiveProviderProfile();
        settings.ApiBase = active.ApiBase;
        settings.Model = active.Model;
        settings.ApiKey = string.Empty;
        try
        {
            var mutations = _pendingApiKeys.Select(pair => pair.Value is null
                    ? SecretMutation.Remove(pair.Key)
                    : SecretMutation.Replace(pair.Key, pair.Value.Trim()))
                .Concat(_removedSecretIds.Select(SecretMutation.Remove))
                .ToList();
            SecretMutationBatch.Execute(_secretStore, mutations, () => App.ConfigService.Save(settings));
        }
        catch (Exception exception)
        {
            try
            {
                App.HotkeyService.Stop();
                var restored = App.HotkeyService.SetHotkeys(App.BuildHotkeyBindings(previousSettings));
                if (System.Windows.Application.Current is App app) app.RecordHotkeyRegistration(restored);
            }
            catch { }
            ValidationMessage = exception.Message;
            return false;
        }
        App.ReplaceSettings(settings);
        _activeProviderProfileId = settings.ActiveProviderProfileId;
        _pendingApiKeys.Clear();
        _removedSecretIds.Clear();
        _apiKey = string.Empty;
        OnPropertyChanged(nameof(ApiKey));
        OnPropertyChanged(nameof(HasStoredApiKey));
        OnPropertyChanged(nameof(ApiKeyStateText));
        OnPropertyChanged(nameof(ApiKeyStatusKind));
        OnPropertyChanged(nameof(FilteredProviderProfiles));
        OnPropertyChanged(nameof(ActiveProviderProfileId));
        OnPropertyChanged(nameof(IsSelectedProviderActive));
        if (!string.Equals(previousDataRoot, App.DataRoot, StringComparison.OrdinalIgnoreCase))
        {
            ValidationMessage = string.IsNullOrWhiteSpace(ValidationMessage)
                ? "数据目录已保存，将在重启 Vesper 后生效；本次会话仍使用原目录。"
                : ValidationMessage + "；数据目录将在重启后生效。";
        }
        ThemeService.Apply(settings, App.SkinService, System.Windows.Application.Current?.Resources);
        if (System.Windows.Application.Current is App currentApp) currentApp.ApplyResidentSettings();
        StartupService.TrySetEnabled(settings.StartWithWindows, out _);

        HasChanges = false;
        _providerConnectionChanged = false;
        CanSaveWithoutVerification = false;
        return true;
    }

    [RelayCommand]
    private void Cancel()
    {
        try
        {
            if (System.Windows.Application.Current is not null)
            {
                ThemeService.Apply(_originalDisplaySettings, App.SkinService, System.Windows.Application.Current.Resources);
                if (System.Windows.Application.Current is App app) app.ApplyDisplaySettings(_originalDisplaySettings);
            }
        }
        catch { }
        HasChanges = false;
    }

    private void LoadFromSettings()
    {
        var settings = App.Settings;
        settings.NormalizeProductModes();
        settings.NormalizeProviderProfiles();
        settings.NormalizePromptSettings();
        _defaultCategory = settings.GetDefaultCategory();
        _defaultDepth = settings.GetDefaultDepth();
        _promptHistoryLimit = settings.PromptHistoryLimit;
        PromptCategoryOptions.Clear();
        foreach (var category in PromptCategoryMetadata.AllCategories)
        {
            PromptCategoryOptions.Add(new PromptCategoryOption(category, settings.EnabledPromptCategories.Contains(category)));
        }
        _hotkey = settings.Hotkey;
        _quickPolishHotkey = settings.QuickPolishHotkey;
        _quickPromptHotkey = settings.QuickPromptHotkey;
        _copyResultHotkey = settings.CopyResultHotkey;
        _defaultMode = settings.DefaultMode;
        _clipboardAutoRead = settings.ClipboardAutoRead;
        _startWithWindows = settings.StartWithWindows;
        _floatingBallEnabled = settings.FloatingBallEnabled;
        _trayEnabled = settings.TrayEnabled;
        _alwaysOnTop = settings.AlwaysOnTop;
        _autoArchive = settings.AutoArchive;
        _historyEnabled = settings.HistoryEnabled;
        _historyRetentionDays = settings.HistoryRetentionDays;
        _saveOriginalText = settings.SaveOriginalText;
        _saveOptimizedText = settings.SaveOptimizedText;
        _incognitoMode = settings.IncognitoMode;
        _autoCopyAfterOptimize = settings.AutoCopyAfterOptimize;
        _enterToSend = settings.EnterToSend;
        _autosaveDelayMilliseconds = settings.AutosaveDelayMilliseconds;
        _clarificationEnabled = settings.ClarificationEnabled;
        _showDiff = settings.ShowDiff;
        _defaultPolishScenario = settings.DefaultPolishScenario;
        _persona = settings.UserPersona;
        _outputStyle = settings.OutputStyle;
        _customStyleInstructions = settings.CustomStyleInstructions;
        _customSystemPrompt = settings.CustomSystemPrompt;
        _preferenceLearningEnabled = settings.PreferenceLearningEnabled;
        _preserveMeaning = settings.PreserveMeaning;
        _minimalRewrite = settings.MinimalRewrite;
        _professionalTone = settings.ProfessionalTone;
        OptimizationPresets.Clear();
        foreach (var preset in settings.OptimizationPresets) OptimizationPresets.Add(preset.Clone());
        _selectedOptimizationPreset = OptimizationPresets.FirstOrDefault(preset => preset.Id == settings.ActivePresetId)
            ?? OptimizationPresets.FirstOrDefault();
        _updateCheckUrl = settings.UpdateCheckUrl;
        _autoCheckUpdates = settings.AutoCheckUpdates;
        _dataDirectory = App.DataRoot;
        _themeMode = settings.ThemeMode;
        _skinId = string.IsNullOrWhiteSpace(settings.SkinId) ? "default" : settings.SkinId;
        _editorFontSize = settings.EditorFontSize;
        _uiScale = settings.UiScale;
            _editorDefaultHeight = settings.EditorDefaultHeight;
        _animationsEnabled = settings.AnimationsEnabled;
        _companionDriverMode = settings.CompanionDriverMode;
            _windowOpacity = settings.WindowOpacity;
            _floatingBallOpacity = settings.FloatingBallOpacity;
            _floatingBallSize = settings.FloatingBallSize;
        _closeBehavior = settings.CloseBehavior;
        _escapeBehavior = settings.EscapeBehavior;
        _rememberWindowSize = settings.RememberWindowSize;
        _rememberFloatingBallPosition = settings.RememberFloatingBallPosition;
        _snapFloatingBallToEdge = settings.SnapFloatingBallToEdge;
        _showInTaskbar = settings.ShowInTaskbar;
        foreach (var profile in settings.ProviderProfiles)
        {
            ProviderProfiles.Add(profile.Clone());
        }
        _activeProviderProfileId = settings.ActiveProviderProfileId;
        _selectedProviderProfile = ProviderProfiles.First(profile => profile.Id == settings.ActiveProviderProfileId);
        _apiKey = string.Empty;
        HasChanges = false;
        OnPropertyChanged(nameof(FilteredProviderProfiles));
        OnPropertyChanged(nameof(CanDuplicateOrEditProvider));
        OnPropertyChanged(nameof(CanRemoveProvider));
    }

    private void SetDirty<T>(ref T field, T value)
    {
        // 关键：必须先调用 SetProperty 触发 PropertyChanged，再置脏标。
        if (SetProperty(ref field, value)) HasChanges = true;
    }

    private void ValidateHotkeysLive()
    {
        var validation = HotkeyBindingSet.Validate(new Dictionary<GlobalHotkeyAction, string>
        {
            [GlobalHotkeyAction.ToggleWindow] = Hotkey,
            [GlobalHotkeyAction.QuickPolish] = QuickPolishHotkey,
            [GlobalHotkeyAction.QuickPromptOptimize] = QuickPromptHotkey,
            [GlobalHotkeyAction.CopyResult] = CopyResultHotkey
        });
        ValidationMessage = validation.IsValid ? string.Empty : validation.ErrorMessage;
    }

    private void ApplyPreview()
    {
        try
        {
            var preview = App.Settings.Clone();
            preview.ThemeMode = _themeMode;
            preview.SkinId = _skinId;
            preview.EditorFontSize = _editorFontSize;
            preview.UiScale = _uiScale;
            preview.EditorDefaultHeight = _editorDefaultHeight;
            preview.AnimationsEnabled = _animationsEnabled;
            preview.WindowOpacity = _windowOpacity;
            preview.FloatingBallOpacity = _floatingBallOpacity;
            preview.FloatingBallSize = _floatingBallSize;
            preview.NormalizeDisplaySettings();
            if (System.Windows.Application.Current is not null)
            {
                ThemeService.Apply(preview, App.SkinService, System.Windows.Application.Current.Resources);
                if (System.Windows.Application.Current is App app) app.ApplyDisplaySettings(preview);
            }
        }
        catch (Exception ex)
        {
            // 不再静默吞掉：若主题/显示预览发生异常，至少能在 Debug 输出里看到根因
            System.Diagnostics.Debug.WriteLine($"[Theme] ApplyPreview failed: {ex}");
        }
    }

    private bool TryParseArchiveDates(out DateTimeOffset? from, out DateTimeOffset? to)
    {
        from = null;
        to = null;
        ArchiveValidationMessage = string.Empty;
        if (!string.IsNullOrWhiteSpace(ArchiveFromText))
        {
            if (!DateTime.TryParseExact(ArchiveFromText.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            {
                ArchiveValidationMessage = "开始日期格式无效，请使用 yyyy-MM-dd。";
                return false;
            }
            from = new DateTimeOffset(value.Date);
        }
        if (!string.IsNullOrWhiteSpace(ArchiveToText))
        {
            if (!DateTime.TryParseExact(ArchiveToText.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            {
                ArchiveValidationMessage = "结束日期格式无效，请使用 yyyy-MM-dd。";
                return false;
            }
            to = new DateTimeOffset(value.Date.AddDays(1).AddTicks(-1));
        }
        return true;
    }

    private void RefreshDataStatus()
    {
        var stats = _dataManagement.GetStatistics(App.DataRoot, _archiveService);
        var size = stats.TotalBytes < 1024 * 1024
            ? $"{stats.TotalBytes / 1024d:0.0} KB"
            : $"{stats.TotalBytes / 1024d / 1024d:0.0} MB";
        var updated = stats.LastUpdatedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "无";
        DataStatus = $"当前筛选 {ArchiveItems.Count} 条 · 全库 {stats.RevisionCount} 条 · {size} · 最近更新 {updated}";
    }

    private void LoadHealthReport(HealthCheckReport? report)
    {
        HealthItems.Clear();
        if (report is null)
        {
            HealthSummary = "尚未执行健康检查";
            return;
        }
        foreach (var item in report.Items) HealthItems.Add(item);
        HealthSummary = report.Summary;
    }
}

public sealed class PromptCategoryOption : ObservableObject
{
    private bool _enabled;

    public PromptCategoryOption(PromptCategory category, bool enabled)
    {
        Category = category;
        _enabled = enabled;
    }

    public PromptCategory Category { get; }
    public string Name => Category.GetDisplayName();
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
}

public sealed record ArchiveModeFilterOption(string Name, ApplicationMode? Mode);

public sealed record SettingOption(string Value, string Name);
public sealed record CompanionDriverModeOption(CompanionDriverMode Value, string Name, string Description);
