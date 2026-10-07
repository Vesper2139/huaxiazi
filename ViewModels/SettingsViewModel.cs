using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Huaxiazi.Models;
using Huaxiazi.Services;

namespace Huaxiazi.ViewModels;

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

public sealed record InferenceLevelOption(InferenceLevel Value, string DisplayName, string Description);
public sealed record ProviderProtocolOption(ProviderProtocol Value, string DisplayName);
public sealed record ProviderRoutingModeOption(ProviderRoutingMode Value, string DisplayName, string Description);
public sealed record ExpressionPreferenceTaskOption(ApplicationMode Value, string DisplayName);
public sealed record PreferenceValueOption(string Value, string DisplayName);

public sealed partial class SettingsViewModel : ObservableObject
{
    public IReadOnlyList<ProviderProtocolOption> OpenAiProtocolOptions { get; } =
    [
        new(ProviderProtocol.OpenAICompatible, "Chat Completions（兼容旧配置）"),
        new(ProviderProtocol.OpenAIResponses, "Responses API")
    ];
    public IReadOnlyList<ExpressionPreferenceTaskOption> ExpressionPreferenceTasks { get; } =
    [new(ApplicationMode.Polish, "表达润色"), new(ApplicationMode.PromptOptimize, "提示词优化")];
    public IReadOnlyList<PreferenceValueOption> ExpressionPreferenceScenarios
    {
        get
        {
            var values = ExpressionPreferenceTask == ApplicationMode.Polish
                ? PolishScenarios.Where(value => !string.Equals(value, "其他", StringComparison.Ordinal)).ToArray()
                : PromptCategoryMetadata.AllCategories.Select(category => category.GetDisplayName()).ToArray();
            return [new(string.Empty, "全部场景"), .. values.Select(value => new PreferenceValueOption(value, value))];
        }
    }
    public IReadOnlyList<PreferenceValueOption> ExpressionLengthOptions { get; } =
    [new("balanced", "均衡"), new("concise", "简洁"), new("detailed", "完整")];
    public IReadOnlyList<PreferenceValueOption> ScopedOutputStyleOptions { get; } =
        [new(string.Empty, "跟随任务/全局"), .. OutputStyleCatalog.Values.Select(value => new PreferenceValueOption(value, value))];
    public IReadOnlyList<PreferenceValueOption> ExpressionToneOptions { get; } =
    [new("natural", "自然"), new("professional", "专业克制"), new("warm", "温和亲切")];
    private Dictionary<string, ExpressionPreferenceSet> _expressionPreferenceDrafts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _editedExpressionPreferenceTasks = new(StringComparer.Ordinal);
    private Dictionary<string, string> _outputStyleOverrideDrafts = new(StringComparer.Ordinal);
    private string _scopedOutputStyle = string.Empty;
    public string ScopedOutputStyle
    {
        get => _scopedOutputStyle;
        set
        {
            var normalized = OutputStyleCatalog.Values.Contains(value, StringComparer.Ordinal) ? value : string.Empty;
            SetDirty(ref _scopedOutputStyle, normalized);
        }
    }
    private ApplicationMode _expressionPreferenceTask = ApplicationMode.Polish;
    private string _expressionPreferenceScenario = string.Empty;
    public string ExpressionPreferenceScenario
    {
        get => _expressionPreferenceScenario;
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(_expressionPreferenceScenario, normalized, StringComparison.Ordinal)) return;
            StoreExpressionPreferenceEditor();
            StoreOutputStyleOverrideEditor();
            if (!SetProperty(ref _expressionPreferenceScenario, normalized)) return;
            LoadExpressionPreferenceEditor(ExpressionPreferenceTask);
            LoadOutputStyleOverrideEditor(ExpressionPreferenceTask);
        }
    }
    public ApplicationMode ExpressionPreferenceTask
    {
        get => _expressionPreferenceTask;
        set
        {
            if (_expressionPreferenceTask == value) return;
            StoreExpressionPreferenceEditor();
            StoreOutputStyleOverrideEditor();
            if (!SetProperty(ref _expressionPreferenceTask, value)) return;
            OnPropertyChanged(nameof(ExpressionPreferenceScenarios));
            if (!ExpressionPreferenceScenarios.Any(option => option.Value == ExpressionPreferenceScenario))
            {
                _expressionPreferenceScenario = string.Empty;
                OnPropertyChanged(nameof(ExpressionPreferenceScenario));
            }
            LoadExpressionPreferenceEditor(value);
            LoadOutputStyleOverrideEditor(value);
        }
    }
    private string _preferredExpressionLength = "balanced";
    public string PreferredExpressionLength
    {
        get => _preferredExpressionLength;
        set { if (SetProperty(ref _preferredExpressionLength, value)) MarkExpressionPreferenceEdited(); }
    }
    private string _preferredExpressionTone = "natural";
    public string PreferredExpressionTone
    {
        get => _preferredExpressionTone;
        set { if (SetProperty(ref _preferredExpressionTone, value)) MarkExpressionPreferenceEdited(); }
    }
    private bool _preserveOriginalWording = true;
    public bool PreserveOriginalWording
    {
        get => _preserveOriginalWording;
        set { if (SetProperty(ref _preserveOriginalWording, value)) MarkExpressionPreferenceEdited(); }
    }
    private string _forbiddenExpressionText = string.Empty;
    public string ForbiddenExpressionText
    {
        get => _forbiddenExpressionText;
        set { if (SetProperty(ref _forbiddenExpressionText, value ?? string.Empty)) MarkExpressionPreferenceEdited(); }
    }

    public IReadOnlyList<ProviderRoutingModeOption> ProviderRoutingModes { get; } =
    [
        new(ProviderRoutingMode.Manual, "手动选择", "沿用主窗口选择的模型；可为润色和提示词优化分别绑定模型。"),
        new(ProviderRoutingMode.Automatic, "自动按任务档位路由", "根据任务复杂度选择 Fast、Balanced 或 Reasoning 档；每种任务的三档模型都需由你明确绑定。路由是启发式建议，不代表已验证的质量或速度排序。"),
        new(ProviderRoutingMode.LocalOnly, "仅本地", "只允许内置运行时或回环地址上的本地服务；没有本地模型时直接报错，不会请求云端。"),
        new(ProviderRoutingMode.PreferLocal, "优先本地", "有本地模型时优先使用；只有配置明确的备用模型时才允许改用云端。"),
        new(ProviderRoutingMode.PreferCloud, "优先云端", "有云端模型时优先使用；本地作为备用时必须明确配置。")
    ];

    private ProviderRoutingMode _providerRoutingMode = ProviderRoutingMode.Manual;
    public ProviderRoutingMode ProviderRoutingMode
    {
        get => _providerRoutingMode;
        set
        {
            if (!SetProperty(ref _providerRoutingMode, value)) return;
            HasChanges = true;
            OnPropertyChanged(nameof(ProviderRoutingDescription));
            OnPropertyChanged(nameof(IsAutomaticRoutingEnabled));
        }
    }
    public string ProviderRoutingDescription => ProviderRoutingModes.FirstOrDefault(option => option.Value == ProviderRoutingMode)?.Description ?? string.Empty;
    public bool IsAutomaticRoutingEnabled => ProviderRoutingMode == ProviderRoutingMode.Automatic;
    private string _polishProviderProfileId = string.Empty;
    public string PolishProviderProfileId { get => _polishProviderProfileId; set => SetDirty(ref _polishProviderProfileId, value ?? string.Empty); }
    private string _promptOptimizeProviderProfileId = string.Empty;
    public string PromptOptimizeProviderProfileId { get => _promptOptimizeProviderProfileId; set => SetDirty(ref _promptOptimizeProviderProfileId, value ?? string.Empty); }
    private string _fallbackProviderProfileId = string.Empty;
    public string FallbackProviderProfileId { get => _fallbackProviderProfileId; set => SetDirty(ref _fallbackProviderProfileId, value ?? string.Empty); }

    private string _polishFastProviderProfileId = string.Empty;
    public string PolishFastProviderProfileId { get => _polishFastProviderProfileId; set => SetDirty(ref _polishFastProviderProfileId, value ?? string.Empty); }
    private string _polishBalancedProviderProfileId = string.Empty;
    public string PolishBalancedProviderProfileId { get => _polishBalancedProviderProfileId; set => SetDirty(ref _polishBalancedProviderProfileId, value ?? string.Empty); }
    private string _polishReasoningProviderProfileId = string.Empty;
    public string PolishReasoningProviderProfileId { get => _polishReasoningProviderProfileId; set => SetDirty(ref _polishReasoningProviderProfileId, value ?? string.Empty); }
    private string _promptOptimizeFastProviderProfileId = string.Empty;
    public string PromptOptimizeFastProviderProfileId { get => _promptOptimizeFastProviderProfileId; set => SetDirty(ref _promptOptimizeFastProviderProfileId, value ?? string.Empty); }
    private string _promptOptimizeBalancedProviderProfileId = string.Empty;
    public string PromptOptimizeBalancedProviderProfileId { get => _promptOptimizeBalancedProviderProfileId; set => SetDirty(ref _promptOptimizeBalancedProviderProfileId, value ?? string.Empty); }
    private string _promptOptimizeReasoningProviderProfileId = string.Empty;
    public string PromptOptimizeReasoningProviderProfileId { get => _promptOptimizeReasoningProviderProfileId; set => SetDirty(ref _promptOptimizeReasoningProviderProfileId, value ?? string.Empty); }

    [RelayCommand]
    private void ClearAutomaticTierBindings()
    {
        PolishFastProviderProfileId = string.Empty;
        PolishBalancedProviderProfileId = string.Empty;
        PolishReasoningProviderProfileId = string.Empty;
        PromptOptimizeFastProviderProfileId = string.Empty;
        PromptOptimizeBalancedProviderProfileId = string.Empty;
        PromptOptimizeReasoningProviderProfileId = string.Empty;
    }

    [RelayCommand] private void ClearPolishProviderBinding() => PolishProviderProfileId = string.Empty;
    [RelayCommand] private void ClearPromptOptimizeProviderBinding() => PromptOptimizeProviderProfileId = string.Empty;
    [RelayCommand] private void ClearFallbackProviderBinding() => FallbackProviderProfileId = string.Empty;

    private readonly ArchiveService _archiveService;
    private readonly ISecretStore _secretStore;
    private readonly OpenRouterModelCatalogService _openRouterModelCatalogService;
    private IReadOnlyList<ModelDefinition> _openRouterModelCatalog = [];
    private bool _hasLoadedOpenRouterModelCatalog;
    [ObservableProperty] private bool _isRefreshingOpenRouterModels;
    private string _openRouterModelCatalogStatus = "目录尚未刷新；你也可以直接输入 Model ID。";
    private readonly Func<ProviderProfile, string?, CancellationToken, Task<ConnectionTestResult>> _connectionTester;
    private readonly Func<IReadOnlyList<AgentSkillRecord>>? _skillCatalogLoader;
    private readonly Dictionary<string, string?> _pendingApiKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removedSecretIds = new(StringComparer.Ordinal);
    private CancellationTokenSource? _localModelDownloadCancellation;
    private CancellationTokenSource? _localRuntimeDownloadCancellation;
    private CancellationTokenSource? _localRuntimeHealthCheckCancellation;
    private readonly DataManagementService _dataManagement = new();
    private readonly AppSettings _originalDisplaySettings;
    public IReadOnlyList<string> Sections { get; } = BuildSections();
    [ObservableProperty] private string _selectedSection = "表达与生成";
    [ObservableProperty] private ExpressionAbilityPane _expressionAbilityPane = ExpressionAbilityPane.Overview;
    public IReadOnlyList<PromptCategory> Categories => PromptCategoryMetadata.AllCategories;
    public IReadOnlyList<PromptDepth> Depths => PromptDepthMetadata.AllDepths;
    public IReadOnlyList<ApplicationMode> Modes { get; } = [ApplicationMode.Polish, ApplicationMode.PromptOptimize];
    public IReadOnlyList<string> OutputStyles { get; } = OutputStyleCatalog.Values;
    /// <summary>前端可展示的用户调优白名单；系统策略和安全参数不在此集合中。</summary>
    public IReadOnlyList<AgentTuningOption> UserTuningOptions { get; } = AgentTuningExposurePolicy.ForUi();
    public IReadOnlyList<InferenceLevelOption> InferenceLevels { get; } =
    [
        new(InferenceLevel.Low, "低", "使用较低档预设值；Provider 是否支持以模型能力说明为准。"),
        new(InferenceLevel.Medium, "中", "使用中档预设值；Provider 是否支持以模型能力说明为准。"),
        new(InferenceLevel.High, "高", "使用高档预设值；Provider 是否支持以模型能力说明为准。"),
        new(InferenceLevel.Custom, "自定义", "自行调整采样、输出上限与超时；模型推理能力见下方说明。")
    ];
    public IReadOnlyList<string> PolishScenarios { get; } = ["私人沟通", "职场沟通", "公开发布", "正式材料", "其他"];
    public IReadOnlyList<SettingOption> ThemeModes { get; } =
        [new("System", "跟随 Windows"), new("Light", "浅色"), new("Dark", "深色")];
    public IReadOnlyList<CompanionDriverModeOption> CompanionDriverModes { get; } =
    [
        new(CompanionDriverMode.Local, "仅显示状态（推荐）", "根据鼠标、窗口、编辑和生成结果显示状态，不分析回答语气。"),
        new(CompanionDriverMode.EmotionAssistant, "根据语气反馈", "让角色根据回答语气给出反馈；不会增加额外请求，可能略微增加用量。")
    ];
    public ObservableCollection<SkinChoiceOption> SpecialSkins { get; } = [];
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
    public ObservableCollection<InstalledLocalModel> InstalledLocalModels { get; } = [];
    public ObservableCollection<LocalModelDescriptor> AvailableLocalModels { get; } = [];
    public string LocalModelRoot => App.LocalModelStore.Root;
    public bool IsLocalRuntimeAvailable => LocalRuntimeManager.IsRuntimeAvailable(
        LocalRuntimePaths.GetRuntimeRoot());
    public IReadOnlyList<LocalRuntimePackageDescriptor> AvailableLocalRuntimePackages => LocalRuntimePackageCatalog.Packages;
    public bool IsCpuRuntimeInstalled => LocalRuntimePackageService.ResolveInstalledExecutable(LocalRuntimePaths.GetRuntimeRoot(), LocalRuntimeFlavor.Cpu) is not null;
    public bool IsVulkanRuntimeInstalled => LocalRuntimePackageService.ResolveInstalledExecutable(LocalRuntimePaths.GetRuntimeRoot(), LocalRuntimeFlavor.Vulkan) is not null;
    public string LocalRuntimeInstallationSummary => $"CPU：{(IsCpuRuntimeInstalled ? "已安装" : "未安装")} · Vulkan：{(IsVulkanRuntimeInstalled ? "已安装" : "未安装")}";
    private string _localModelCatalogUrl = string.Empty;
    public string LocalModelCatalogUrl { get => _localModelCatalogUrl; set => SetDirty(ref _localModelCatalogUrl, value); }
    [ObservableProperty] private string _localModelStatus = "模型文件不会自动下载。请在本页主动导入或安装本地模型。";
    [ObservableProperty] private bool _isLocalModelDownloading;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallLocalRuntimePackageCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallLocalRuntimePackageCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckInstalledLocalModelCommand))]
    private bool _isLocalRuntimePackageDownloading;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallLocalRuntimePackageCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallLocalRuntimePackageCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckInstalledLocalModelCommand))]
    private bool _isLocalRuntimeHealthCheckRunning;
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
    private bool _localGenerationDiagnosticsEnabled;
    public bool LocalGenerationDiagnosticsEnabled
    {
        get => _localGenerationDiagnosticsEnabled;
        set => SetDirty(ref _localGenerationDiagnosticsEnabled, value);
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

    [RelayCommand]
    private void ClearLocalGenerationDiagnostics()
    {
        try
        {
            new GenerationDiagnosticsService(Path.Combine(App.DataRoot, "diagnostics")).Clear();
            ValidationMessage = "本机生成诊断记录已清除。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ValidationMessage = $"清除本机生成诊断失败：{exception.Message}";
        }
    }

    [RelayCommand]
    private void GenerateLocalGenerationDiagnosticsSummary()
    {
        try
        {
            var diagnostics = new GenerationDiagnosticsService(Path.Combine(App.DataRoot, "diagnostics"));
            var requestCount = diagnostics.ReadRecent().Count;
            var workflowCount = diagnostics.ReadWorkflowRecent().Count;
            var path = new GenerationDiagnosticsReportService(diagnostics).WriteSummary();
            ValidationMessage = $"本机诊断摘要已生成：{Path.GetFileName(path)}（{requestCount} 次请求尝试，{workflowCount} 个工作流）。可从数据目录打开查看。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ValidationMessage = $"生成本机诊断摘要失败：{exception.Message}";
        }
    }
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
            if (PreferenceLearningEnabled && !IncognitoMode)
            {
                new StructuredPreferenceService().RecordStyleChoice(App.Settings.ExpressionPreferenceProfile, value);
                OnPropertyChanged(nameof(PreferenceSummary));
            }
        }
    }
    private string _customStyleInstructions = string.Empty;
    public string CustomStyleInstructions { get => _customStyleInstructions; set => SetDirty(ref _customStyleInstructions, value); }
    private string _customSystemPrompt = string.Empty;
    public string CustomSystemPrompt { get => _customSystemPrompt; set => SetDirty(ref _customSystemPrompt, value); }
    private bool _preferenceLearningEnabled = true;
    public bool PreferenceLearningEnabled { get => _preferenceLearningEnabled; set => SetDirty(ref _preferenceLearningEnabled, value); }
    private bool _shareConfirmedPreferencesWithCloud;
    public bool ShareConfirmedPreferencesWithCloud
    {
        get => _shareConfirmedPreferencesWithCloud;
        set => SetDirty(ref _shareConfirmedPreferencesWithCloud, value);
    }
    public string PreferenceSummary
    {
        get
        {
            var profile = App.Settings.ExpressionPreferenceProfile ?? new ExpressionPreferenceProfile();
            var key = PreferenceTaskKey(ExpressionPreferenceTask, ExpressionPreferenceScenario);
            var confirmed = _expressionPreferenceDrafts.TryGetValue(key, out var preference) && preference.UserConfirmed;
            ExpressionInteractionSignalSet? signals = null;
            profile.InteractionSignals?.TryGetValue(key, out signals);
            var state = _editedExpressionPreferenceTasks.Contains(key)
                ? "当前任务/场景的偏好有未保存修改；保存设置后才会确认并生效。"
                : confirmed ? "当前任务/场景的偏好已由你确认；场景偏好优先于任务通用偏好。" : "当前任务/场景尚无已确认偏好；交互统计只用于分析，不会自动写入提示。";
            var scoped = signals is null
                ? "当前范围交互统计：暂无。"
                : $"当前范围交互统计：接受 {signals.AcceptedCount}、编辑 {signals.EditCount}、重试 {signals.RetryCount}、撤销 {signals.UndoCount}；改短 {signals.ShorteningEdits}、改长 {signals.ExpansionEdits}。";
            var styles = $"全局显式风格设置变更：{profile.StyleChoiceCount} 次（{FormatOutputStyleCounts(profile.StyleChoiceUsage)}）。";
            return state + scoped + styles + "仅保存本地结构化统计，不保存原文或修改后的成稿；统计不会自动成为偏好。";
        }
    }

    public string PreferenceMetadataSummary
    {
        get
        {
            var key = PreferenceTaskKey(ExpressionPreferenceTask, ExpressionPreferenceScenario);
            if (!_expressionPreferenceDrafts.TryGetValue(key, out var preference) || !preference.UserConfirmed)
                return "来源：尚未确认 · 统计候选仅供参考，载入并保存后才会成为用户偏好";
            var updated = preference.UpdatedAtUtc?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? "时间未记录";
            return $"来源：用户确认 · 置信度：{preference.Confidence:0.0} · 更新时间：{updated}";
        }
    }

    public ExpressionPreferenceCandidate? CurrentExpressionPreferenceCandidate =>
        new ExpressionPreferenceSuggestionService().GetLengthCandidate(
            App.Settings.ExpressionPreferenceProfile,
            ExpressionPreferenceTask,
            ExpressionPreferenceScenario);

    public ExpressionPreferenceCandidate? CurrentExpressionToneCandidate =>
        new ExpressionPreferenceSuggestionService().GetToneCandidate(
            App.Settings.ExpressionPreferenceProfile,
            ExpressionPreferenceTask,
            ExpressionPreferenceScenario);

    public bool HasExpressionPreferenceCandidate => CurrentExpressionPreferenceCandidate is not null;
    public bool HasExpressionToneCandidate => CurrentExpressionToneCandidate is not null;

    public string ExpressionPreferenceCandidateSummary
    {
        get
        {
            var candidate = CurrentExpressionPreferenceCandidate;
            if (candidate is null)
            {
                var scopeKey = PreferenceTaskKey(ExpressionPreferenceTask, ExpressionPreferenceScenario);
                ExpressionInteractionSignalSet? signals = null;
                App.Settings.ExpressionPreferenceProfile.InteractionSignals?.TryGetValue(scopeKey, out signals);
                var ignoredCandidate = new ExpressionPreferenceSuggestionService().GetLengthCandidate(
                    App.Settings.ExpressionPreferenceProfile,
                    ExpressionPreferenceTask,
                    ExpressionPreferenceScenario,
                    includeIgnored: true);
                if (ignoredCandidate is not null)
                {
                    var direction = ignoredCandidate.Value == "concise" ? "改短" : "改长";
                    return $"此范围的篇幅候选已忽略（{direction}后被接受的成稿 {ignoredCandidate.SupportingOutputs} 个）；可在其它任务/场景独立管理偏好。";
                }
                if (signals is null)
                    return "当前范围尚无生成后编辑并接受/重试/撤销的关联记录，暂不生成篇幅候选。";
                return $"当前范围的关联记录：接受后改短/改长 {signals.AcceptedShortenedOutputs}/{signals.AcceptedExpandedOutputs} 个，重试/撤销后改短/改长 {signals.RejectedShortenedOutputs}/{signals.RejectedExpandedOutputs} 个；接受风格：{FormatOutputStyleCounts(signals.AcceptedOutputStyles)}；重试/撤销风格：{FormatOutputStyleCounts(signals.RejectedOutputStyles)}。只有输出风格一致且方向单一的接受记录、没有拒绝记录时才显示篇幅候选；混合信号不推断。";
            }

            var value = candidate.Value == "concise" ? "简洁" : "完整";
            var editDirection = candidate.Value == "concise" ? "改短" : "改长";
            var signalsForScope = App.Settings.ExpressionPreferenceProfile.InteractionSignals[candidate.ScopeKey];
            return $"交互线索（未校准）：{candidate.SupportingOutputs} 个生成结果经{editDirection}后被接受；已接受输出风格：{FormatOutputStyleCounts(signalsForScope.AcceptedOutputStyles)}。改短/改长后被接受分别为 {signalsForScope.AcceptedShortenedOutputs}/{signalsForScope.AcceptedExpandedOutputs} 个，重试/撤销关联的改短/改长结果分别为 {signalsForScope.RejectedShortenedOutputs}/{signalsForScope.RejectedExpandedOutputs} 个。当前仅单一输出风格和接受方向，暂建议篇幅“{value}”；这不是质量或偏好概率判断，需由你决定是否采用。";
        }
    }

    public string ExpressionToneCandidateSummary
    {
        get
        {
            var candidate = CurrentExpressionToneCandidate;
            if (candidate is null)
            {
                var ignoredCandidate = new ExpressionPreferenceSuggestionService().GetToneCandidate(
                    App.Settings.ExpressionPreferenceProfile,
                    ExpressionPreferenceTask,
                    ExpressionPreferenceScenario,
                    includeIgnored: true);
                return ignoredCandidate is null
                    ? "只有同一任务/场景至少 3 个已接受结果持续选择“正式/专业”或“亲切”风格，且没有拒绝或混合信号时，才显示语气建议；载入并保存前不会影响生成。"
                    : $"此范围的语气候选已忽略（{ignoredCandidate.SupportingOutputs} 个已接受结果）；可在其它任务/场景独立管理偏好。";
            }

            var suggestedTone = candidate.Value == "professional" ? "专业克制" : "温和亲切";
            return $"交互线索（未校准）：{candidate.SupportingOutputs} 个已接受结果使用了同一类输出风格；仅建议语气“{suggestedTone}”。这是弱提示，不代表质量或偏好概率判断，需由你决定是否采用。";
        }
    }

    public IReadOnlyList<ExpressionPreferenceCandidate> ForbiddenExpressionCandidates =>
        new ExpressionPreferenceSuggestionService().GetForbiddenExpressionCandidates(
            App.Settings.ExpressionPreferenceProfile,
            ExpressionPreferenceTask,
            ExpressionPreferenceScenario);

    public bool HasForbiddenExpressionCandidates => ForbiddenExpressionCandidates.Count > 0;

    public string ForbiddenExpressionCandidateSummary => HasForbiddenExpressionCandidates
        ? "以下固定表达在至少 3 个已接受结果中被删除，且没有相反反馈。它们只是可编辑候选；载入并保存后才会成为当前范围的偏好。"
        : "至少 3 次在已接受结果中删除同一固定表达、且没有相反反馈时，才显示可选候选；原文和成稿不会保存在这些统计中。";

    private static string PreferenceTaskKey(ApplicationMode mode, string? scenario = null) =>
        StructuredPreferenceService.GetTaskPreferenceKey(mode, scenario);

    private static string FormatOutputStyleCounts(Dictionary<string, int>? counts) =>
        counts is null || counts.Count == 0
            ? "暂无"
            : string.Join("、", counts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key} {pair.Value} 次"));

    private void MarkExpressionPreferenceEdited()
    {
        HasChanges = true;
        var key = PreferenceTaskKey(ExpressionPreferenceTask, ExpressionPreferenceScenario);
        _editedExpressionPreferenceTasks.Add(key);
        StoreExpressionPreferenceEditor();
        OnPropertyChanged(nameof(PreferenceSummary));
        OnPropertyChanged(nameof(PreferenceMetadataSummary));
        OnPropertyChanged(nameof(ExpressionPreferenceCandidateSummary));
        OnPropertyChanged(nameof(HasExpressionPreferenceCandidate));
        OnPropertyChanged(nameof(ExpressionToneCandidateSummary));
        OnPropertyChanged(nameof(HasExpressionToneCandidate));
        OnPropertyChanged(nameof(ForbiddenExpressionCandidates));
        OnPropertyChanged(nameof(HasForbiddenExpressionCandidates));
        OnPropertyChanged(nameof(ForbiddenExpressionCandidateSummary));
    }

    private void StoreExpressionPreferenceEditor()
    {
        var key = PreferenceTaskKey(ExpressionPreferenceTask, ExpressionPreferenceScenario);
        if (!_editedExpressionPreferenceTasks.Contains(key) && !_expressionPreferenceDrafts.ContainsKey(key)) return;
        var set = _expressionPreferenceDrafts.TryGetValue(key, out var existing)
            ? existing
            : new ExpressionPreferenceSet();
        set.PreferredLength = PreferredExpressionLength;
        set.PreferredTone = PreferredExpressionTone;
        set.PreserveOriginalWording = PreserveOriginalWording;
        set.ForbiddenExpressions = ForbiddenExpressionText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
        _expressionPreferenceDrafts[key] = set;
    }

    private void LoadExpressionPreferenceEditor(ApplicationMode mode)
    {
        var key = PreferenceTaskKey(mode, ExpressionPreferenceScenario);
        var set = _expressionPreferenceDrafts.TryGetValue(key, out var existing)
            ? existing
            : new ExpressionPreferenceSet();
        _preferredExpressionLength = set.PreferredLength;
        _preferredExpressionTone = set.PreferredTone;
        _preserveOriginalWording = set.PreserveOriginalWording;
        _forbiddenExpressionText = string.Join(Environment.NewLine, set.ForbiddenExpressions);
        OnPropertyChanged(nameof(PreferredExpressionLength));
        OnPropertyChanged(nameof(PreferredExpressionTone));
        OnPropertyChanged(nameof(PreserveOriginalWording));
        OnPropertyChanged(nameof(ForbiddenExpressionText));
        OnPropertyChanged(nameof(PreferenceSummary));
        OnPropertyChanged(nameof(PreferenceMetadataSummary));
        OnPropertyChanged(nameof(ExpressionPreferenceCandidateSummary));
        OnPropertyChanged(nameof(HasExpressionPreferenceCandidate));
        OnPropertyChanged(nameof(ExpressionToneCandidateSummary));
        OnPropertyChanged(nameof(HasExpressionToneCandidate));
        OnPropertyChanged(nameof(ForbiddenExpressionCandidates));
        OnPropertyChanged(nameof(HasForbiddenExpressionCandidates));
        OnPropertyChanged(nameof(ForbiddenExpressionCandidateSummary));
    }

    private void StoreOutputStyleOverrideEditor()
    {
        var key = OutputStylePreferenceResolver.CreateKey(ExpressionPreferenceTask, ExpressionPreferenceScenario);
        if (string.IsNullOrWhiteSpace(ScopedOutputStyle))
            _outputStyleOverrideDrafts.Remove(key);
        else
            _outputStyleOverrideDrafts[key] = ScopedOutputStyle;
    }

    private void LoadOutputStyleOverrideEditor(ApplicationMode mode)
    {
        var key = OutputStylePreferenceResolver.CreateKey(mode, ExpressionPreferenceScenario);
        _scopedOutputStyle = _outputStyleOverrideDrafts.TryGetValue(key, out var style) ? style : string.Empty;
        OnPropertyChanged(nameof(ScopedOutputStyle));
    }

    private static ExpressionPreferenceSet ClonePreferenceSet(ExpressionPreferenceSet source) => new()
    {
        PreferredLength = source.PreferredLength,
        PreferredTone = source.PreferredTone,
        PreserveOriginalWording = source.PreserveOriginalWording,
        ForbiddenExpressions = [.. source.ForbiddenExpressions],
        UserConfirmed = source.UserConfirmed,
        Source = source.Source,
        Confidence = source.Confidence,
        UpdatedAtUtc = source.UpdatedAtUtc
    };

    [RelayCommand]
    private void ResetExpressionPreferences()
    {
        var previousProfile = App.Settings.ExpressionPreferenceProfile;
        App.Settings.ExpressionPreferenceProfile = new ExpressionPreferenceProfile();
        try
        {
            App.ConfigService.Save(App.Settings);
        }
        catch (Exception exception)
        {
            App.Settings.ExpressionPreferenceProfile = previousProfile;
            OnPropertyChanged(nameof(PreferenceSummary));
            OnPropertyChanged(nameof(PreferenceMetadataSummary));
            OnPropertyChanged(nameof(ExpressionPreferenceCandidateSummary));
            OnPropertyChanged(nameof(HasExpressionPreferenceCandidate));
            OnPropertyChanged(nameof(ExpressionToneCandidateSummary));
            OnPropertyChanged(nameof(HasExpressionToneCandidate));
            ValidationMessage = $"偏好重置失败，本地数据已保留：{exception.Message}";
            return;
        }

        _expressionPreferenceDrafts = new Dictionary<string, ExpressionPreferenceSet>(StringComparer.Ordinal);
        _editedExpressionPreferenceTasks.Clear();
        LoadExpressionPreferenceEditor(ExpressionPreferenceTask);
        OnPropertyChanged(nameof(PreferenceSummary));
        OnPropertyChanged(nameof(PreferenceMetadataSummary));
        OnPropertyChanged(nameof(ExpressionPreferenceCandidateSummary));
        OnPropertyChanged(nameof(HasExpressionPreferenceCandidate));
        OnPropertyChanged(nameof(ExpressionToneCandidateSummary));
        OnPropertyChanged(nameof(HasExpressionToneCandidate));
        OnPropertyChanged(nameof(ForbiddenExpressionCandidates));
        OnPropertyChanged(nameof(HasForbiddenExpressionCandidates));
        OnPropertyChanged(nameof(ForbiddenExpressionCandidateSummary));
        ValidationMessage = "本机偏好与交互统计已重置并保存；此前导出的 JSON 和手动备份 ZIP 不会被改动，如需从副本中清除请手动删除。";
    }

    [RelayCommand]
    private void ApplyExpressionPreferenceCandidate()
    {
        var candidate = CurrentExpressionPreferenceCandidate;
        if (candidate is null) return;

        PreferredExpressionLength = candidate.Value;
        ValidationMessage = "候选已载入当前编辑范围；保存设置后才会确认并用于生成。";
        OnPropertyChanged(nameof(ExpressionPreferenceCandidateSummary));
        OnPropertyChanged(nameof(HasExpressionPreferenceCandidate));
    }

    [RelayCommand]
    private void ApplyExpressionToneCandidate()
    {
        var candidate = CurrentExpressionToneCandidate;
        if (candidate is null) return;

        PreferredExpressionTone = candidate.Value;
        ValidationMessage = "语气候选已载入当前编辑范围；保存设置后才会确认并用于生成。";
        OnPropertyChanged(nameof(ExpressionToneCandidateSummary));
        OnPropertyChanged(nameof(HasExpressionToneCandidate));
    }

    [RelayCommand]
    private void ApplyForbiddenExpressionCandidate(ExpressionPreferenceCandidate? candidate)
    {
        if (candidate is null || !ForbiddenExpressionCandidates.Any(item => item.Key == candidate.Key)) return;
        var existing = ForbiddenExpressionText
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim());
        if (existing.Contains(candidate.Value, StringComparer.OrdinalIgnoreCase)) return;

        ForbiddenExpressionText = string.Join(Environment.NewLine, existing.Append(candidate.Value));
        ValidationMessage = $"已将“{candidate.Value}”载入当前范围的禁用表达编辑框；可修改或删除，保存设置后才会确认。";
    }

    [RelayCommand]
    private void ClearCurrentExpressionPreference()
    {
        var key = PreferenceTaskKey(ExpressionPreferenceTask, ExpressionPreferenceScenario);
        _expressionPreferenceDrafts.Remove(key);
        _editedExpressionPreferenceTasks.Remove(key);
        _preferredExpressionLength = "balanced";
        _preferredExpressionTone = "natural";
        _preserveOriginalWording = true;
        _forbiddenExpressionText = string.Empty;
        OnPropertyChanged(nameof(PreferredExpressionLength));
        OnPropertyChanged(nameof(PreferredExpressionTone));
        OnPropertyChanged(nameof(PreserveOriginalWording));
        OnPropertyChanged(nameof(ForbiddenExpressionText));
        OnPropertyChanged(nameof(PreferenceSummary));
        OnPropertyChanged(nameof(PreferenceMetadataSummary));
        OnPropertyChanged(nameof(ExpressionPreferenceCandidateSummary));
        OnPropertyChanged(nameof(HasExpressionPreferenceCandidate));
        OnPropertyChanged(nameof(ExpressionToneCandidateSummary));
        OnPropertyChanged(nameof(HasExpressionToneCandidate));
        OnPropertyChanged(nameof(ForbiddenExpressionCandidates));
        OnPropertyChanged(nameof(HasForbiddenExpressionCandidates));
        OnPropertyChanged(nameof(ForbiddenExpressionCandidateSummary));
        HasChanges = true;
        ValidationMessage = "当前任务/场景偏好已标记为删除；保存设置后生效。交互统计和其它范围偏好会保留。";
    }

    [RelayCommand]
    private void IgnoreExpressionPreferenceCandidate()
    {
        var candidate = CurrentExpressionPreferenceCandidate;
        if (candidate is null) return;

        var ignoredKeys = App.Settings.ExpressionPreferenceProfile.IgnoredSuggestionKeys;
        if (ignoredKeys.Contains(candidate.Key, StringComparer.Ordinal)) return;
        var evictedKey = ignoredKeys.Count >= 128 ? ignoredKeys[0] : null;
        if (evictedKey is not null) ignoredKeys.RemoveAt(0);
        ignoredKeys.Add(candidate.Key);
        try
        {
            App.ConfigService.Save(App.Settings);
            ValidationMessage = "已在本机忽略此篇幅候选；后续交互方向发生变化时仍可能出现新的候选。";
        }
        catch (Exception exception)
        {
            ignoredKeys.Remove(candidate.Key);
            if (evictedKey is not null) ignoredKeys.Insert(0, evictedKey);
            ValidationMessage = $"忽略候选未能保存，本次忽略未生效：{exception.Message}";
        }

        OnPropertyChanged(nameof(ExpressionPreferenceCandidateSummary));
        OnPropertyChanged(nameof(HasExpressionPreferenceCandidate));
            OnPropertyChanged(nameof(ForbiddenExpressionCandidates));
            OnPropertyChanged(nameof(HasForbiddenExpressionCandidates));
            OnPropertyChanged(nameof(ForbiddenExpressionCandidateSummary));
    }

    [RelayCommand]
    private void IgnoreExpressionToneCandidate()
    {
        var candidate = CurrentExpressionToneCandidate;
        if (candidate is null) return;

        var ignoredKeys = App.Settings.ExpressionPreferenceProfile.IgnoredSuggestionKeys;
        if (ignoredKeys.Contains(candidate.Key, StringComparer.Ordinal)) return;
        var evictedKey = ignoredKeys.Count >= 128 ? ignoredKeys[0] : null;
        if (evictedKey is not null) ignoredKeys.RemoveAt(0);
        ignoredKeys.Add(candidate.Key);
        try
        {
            App.ConfigService.Save(App.Settings);
            ValidationMessage = "已在本机忽略此语气候选；其它任务/场景的建议仍可单独管理。";
        }
        catch (Exception exception)
        {
            ignoredKeys.Remove(candidate.Key);
            if (evictedKey is not null) ignoredKeys.Insert(0, evictedKey);
            ValidationMessage = $"忽略候选未能保存，本次忽略未生效：{exception.Message}";
        }

        OnPropertyChanged(nameof(ExpressionToneCandidateSummary));
        OnPropertyChanged(nameof(HasExpressionToneCandidate));
    }

    [RelayCommand]
    private void IgnoreForbiddenExpressionCandidate(ExpressionPreferenceCandidate? candidate)
    {
        if (candidate is null || !ForbiddenExpressionCandidates.Any(item => item.Key == candidate.Key)) return;
        var ignoredKeys = App.Settings.ExpressionPreferenceProfile.IgnoredSuggestionKeys;
        if (ignoredKeys.Contains(candidate.Key, StringComparer.Ordinal)) return;
        var evictedKey = ignoredKeys.Count >= 128 ? ignoredKeys[0] : null;
        if (evictedKey is not null) ignoredKeys.RemoveAt(0);
        ignoredKeys.Add(candidate.Key);
        try
        {
            App.ConfigService.Save(App.Settings);
            ValidationMessage = $"已在本机忽略“{candidate.Value}”候选；其它表达候选仍可单独管理。";
        }
        catch (Exception exception)
        {
            ignoredKeys.Remove(candidate.Key);
            if (evictedKey is not null) ignoredKeys.Insert(0, evictedKey);
            ValidationMessage = $"忽略候选未能保存，本次忽略未生效：{exception.Message}";
        }

        OnPropertyChanged(nameof(ForbiddenExpressionCandidates));
        OnPropertyChanged(nameof(HasForbiddenExpressionCandidates));
        OnPropertyChanged(nameof(ForbiddenExpressionCandidateSummary));
    }

    public void ExportExpressionPreferences(string destinationPath)
    {
        try
        {
            new ExpressionPreferenceExportService().Export(
                App.Settings.ExpressionPreferenceProfile,
                destinationPath,
                App.Settings.OutputStyle,
                App.Settings.OutputStyleOverrides);
            ValidationMessage = "已导出已保存的本地表达偏好和输出风格（含无文本内容的交互统计）。";
        }
        catch (Exception exception)
        {
            ValidationMessage = $"偏好导出失败：{exception.Message}";
        }
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
        ReloadSkinOptions();
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
    private double _floatingBallSize = CompanionDisplayMetrics.DefaultSize;
    public double FloatingBallSize
    {
        get => _floatingBallSize;
        set
        {
            var normalized = CompanionDisplayMetrics.NormalizeSize(value);
            SetDirty(ref _floatingBallSize, normalized);
            OnPropertyChanged(nameof(DisplayPreviewSummary));
            ApplyPreview();
        }
    }
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
                OnPropertyChanged(nameof(SelectedOpenAiProtocol));
                OnPropertyChanged(nameof(IsOpenAiProtocolSelectorVisible));
                OnPropertyChanged(nameof(IsApiBaseEditable));
                OnPropertyChanged(nameof(AvailableModels));
                OnPropertyChanged(nameof(IsOpenRouterModelRefreshVisible));
                OnPropertyChanged(nameof(SelectedModel));
                OnPropertyChanged(nameof(SelectedProviderHelp));
                OnPropertyChanged(nameof(SelectedProviderWebsite));
                OnPropertyChanged(nameof(SelectedProviderApiKeyUrl));
                OnPropertyChanged(nameof(ModelId));
                OnPropertyChanged(nameof(ApiBaseInput));
                OnPropertyChanged(nameof(SelectedInferenceLevel));
                OnPropertyChanged(nameof(IsCustomInference));
                OnPropertyChanged(nameof(SelectedInferenceDescription));
                OnPropertyChanged(nameof(SelectedProviderCapabilitySummary));
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

    [RelayCommand]
    private void RefreshLocalModels()
    {
        InstalledLocalModels.Clear();
        foreach (var model in App.LocalModelStore.GetInstalledModels()) InstalledLocalModels.Add(model);
        if (App.LocalModelStore.LastRegistryLoadError is not null)
        {
            App.LogApplicationError("LocalModelRegistry", App.LocalModelStore.LastRegistryLoadError);
            LocalModelStatus = "本地模型注册表读取失败，已保留原文件；请查看 errors.log 后恢复或重新导入。";
            return;
        }
        LocalModelStatus = InstalledLocalModels.Count == 0
            ? "尚未安装受管本地模型。"
            : $"已安装 {InstalledLocalModels.Count} 个本地模型。";
        OnPropertyChanged(nameof(IsLocalRuntimeAvailable));
        OnPropertyChanged(nameof(IsCpuRuntimeInstalled));
        OnPropertyChanged(nameof(IsVulkanRuntimeInstalled));
        OnPropertyChanged(nameof(LocalRuntimeInstallationSummary));
    }

    [RelayCommand(CanExecute = nameof(CanInstallLocalRuntimePackage))]
    private async Task InstallLocalRuntimePackageAsync(LocalRuntimePackageDescriptor? descriptor)
    {
        if (descriptor is null || IsLocalRuntimePackageDownloading || IsLocalRuntimeHealthCheckRunning) return;
        IsLocalRuntimePackageDownloading = true;
        _localRuntimeDownloadCancellation?.Dispose();
        _localRuntimeDownloadCancellation = new CancellationTokenSource();
        try
        {
            LocalModelStatus = $"正在下载独立运行时 {descriptor.DisplayName}…";
            var progress = new Progress<LocalRuntimePackageProgress>(value =>
                LocalModelStatus = $"运行时下载 {value.Fraction:P1} · {ByteSizeFormatter.FormatModelPackageSize(value.ReceivedBytes)}/{ByteSizeFormatter.FormatModelPackageSize(value.TotalBytes)}");
            using var service = new LocalRuntimePackageService();
            await service.InstallAsync(descriptor, progress, _localRuntimeDownloadCancellation.Token);
            OnPropertyChanged(nameof(IsLocalRuntimeAvailable));
            OnPropertyChanged(nameof(IsCpuRuntimeInstalled));
            OnPropertyChanged(nameof(IsVulkanRuntimeInstalled));
            OnPropertyChanged(nameof(LocalRuntimeInstallationSummary));
            LocalModelStatus = $"已安装 {descriptor.DisplayName}（prerelease 预览版，编译源码身份未核实）；运行时与主程序分开存放。";
        }
        catch (OperationCanceledException)
        {
            LocalModelStatus = "运行时下载已取消；已下载部分会保留以便续传。";
        }
        catch (Exception exception)
        {
            LocalModelStatus = "运行时安装失败：" + exception.Message;
        }
        finally
        {
            _localRuntimeDownloadCancellation?.Dispose();
            _localRuntimeDownloadCancellation = null;
            IsLocalRuntimePackageDownloading = false;
        }
    }

    private bool CanInstallLocalRuntimePackage(LocalRuntimePackageDescriptor? descriptor) =>
        descriptor is not null && !IsLocalRuntimePackageDownloading && !IsLocalRuntimeHealthCheckRunning;

    [RelayCommand]
    private void CancelLocalRuntimePackageDownload() => _localRuntimeDownloadCancellation?.Cancel();

    [RelayCommand]
    private async Task UninstallLocalRuntimePackageAsync(LocalRuntimePackageDescriptor? descriptor)
    {
        if (descriptor is null || IsLocalRuntimePackageDownloading || IsLocalRuntimeHealthCheckRunning) return;
        try
        {
            App.LocalRuntimeManager.Stop();
            using var service = new LocalRuntimePackageService();
            await service.UninstallAsync(descriptor);
            OnPropertyChanged(nameof(IsLocalRuntimeAvailable));
            OnPropertyChanged(nameof(IsCpuRuntimeInstalled));
            OnPropertyChanged(nameof(IsVulkanRuntimeInstalled));
            OnPropertyChanged(nameof(LocalRuntimeInstallationSummary));
            LocalModelStatus = $"已卸载 {descriptor.DisplayName}。";
        }
        catch (Exception exception)
        {
            LocalModelStatus = "运行时卸载失败：" + exception.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheckInstalledLocalModel))]
    private async Task CheckInstalledLocalModelAsync(InstalledLocalModel? model)
    {
        if (model is null || IsLocalRuntimeHealthCheckRunning || IsLocalRuntimePackageDownloading) return;
        IsLocalRuntimeHealthCheckRunning = true;
        _localRuntimeHealthCheckCancellation?.Dispose();
        _localRuntimeHealthCheckCancellation = new CancellationTokenSource();
        try
        {
            LocalModelStatus = $"正在本机启动并检查 {model.DisplayName}；这会加载模型并使用本机内存，不会请求云端。";
            var profile = ProviderProfiles.FirstOrDefault(item =>
                item.Platform == ProviderPlatform.ManagedLocal && item.LocalModelInstallationId == model.InstallationId)?.Clone()
                ?? new ProviderProfile
                {
                    Id = "runtime-check-" + model.InstallationId,
                    Name = model.DisplayName,
                    Type = ProviderType.Local,
                    Platform = ProviderPlatform.ManagedLocal,
                    Protocol = ProviderProtocol.OpenAICompatible,
                    ApiBase = "http://127.0.0.1:0/v1",
                    Model = model.InstallationId,
                    LocalModelInstallationId = model.InstallationId
                };
            var endpoint = await App.LocalRuntimeManager.EnsureStartedAsync(
                profile, _localRuntimeHealthCheckCancellation.Token);
            var startupMs = App.LocalRuntimeManager.GetMetrics()?.StartupToReadyMilliseconds;
            var timing = startupMs.HasValue ? $"首次启动 {startupMs.Value:0} ms" : "已复用运行中的进程";
            LocalModelStatus = $"{model.DisplayName} 本地服务健康检查通过（{endpoint.DisplayBackend}，{timing}）；运行进程保持开启，可手动停止。未发起云端请求。";
        }
        catch (OperationCanceledException)
        {
            LocalModelStatus = "本地模型启动检查已取消，运行时已清理未就绪进程。";
        }
        catch (Exception exception)
        {
            LocalModelStatus = "本地模型启动检查失败：" + exception.Message;
        }
        finally
        {
            _localRuntimeHealthCheckCancellation?.Dispose();
            _localRuntimeHealthCheckCancellation = null;
            IsLocalRuntimeHealthCheckRunning = false;
        }
    }

    private bool CanCheckInstalledLocalModel(InstalledLocalModel? model) =>
        model is not null && !IsLocalRuntimeHealthCheckRunning && !IsLocalRuntimePackageDownloading;

    [RelayCommand]
    private void CancelLocalRuntimeHealthCheck() => _localRuntimeHealthCheckCancellation?.Cancel();

    [RelayCommand]
    private void StopLocalRuntime()
    {
        App.LocalRuntimeManager.Stop();
        LocalModelStatus = "本地推理服务已停止并释放运行进程。";
    }

    [RelayCommand]
    private async Task RefreshLocalModelCatalogAsync()
    {
        var url = LocalModelCatalogUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            AvailableLocalModels.Clear();
            foreach (var model in LocalModelCatalogService.BuiltInCatalog.Models) AvailableLocalModels.Add(model);
            LocalModelStatus = $"已加载内置开源模型目录，共 {AvailableLocalModels.Count} 个模型；点击下载即可自动校验、安装并配置。";
            return;
        }
        try
        {
            LocalModelStatus = "正在验证精选目录签名…";
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var catalog = await new LocalModelCatalogService().FetchAsync(url, client);
            AvailableLocalModels.Clear();
            foreach (var model in catalog.Models) AvailableLocalModels.Add(model);
            LocalModelStatus = $"已加载精选目录 {catalog.CatalogVersion}，共 {AvailableLocalModels.Count} 个模型。";
        }
        catch (Exception exception)
        {
            LocalModelStatus = "精选目录加载失败：" + exception.Message;
        }
    }

    private bool CanDownloadLocalModel(LocalModelDescriptor? descriptor) => descriptor is not null && !IsLocalModelDownloading;

    [RelayCommand(CanExecute = nameof(CanDownloadLocalModel))]
    private async Task DownloadLocalModelAsync(LocalModelDescriptor? descriptor)
    {
        if (descriptor is null) return;
        if (IsLocalModelDownloading) return;
        IsLocalModelDownloading = true;
        _localModelDownloadCancellation?.Dispose();
        _localModelDownloadCancellation = new CancellationTokenSource();
        try
        {
            LocalModelStatus = $"正在下载 {descriptor.DisplayName}…";
            var progress = new Progress<LocalModelDownloadProgress>(value =>
                LocalModelStatus = $"下载 {value.Fraction:P1} · {ByteSizeFormatter.FormatModelPackageSize(value.ReceivedBytes)}/{ByteSizeFormatter.FormatModelPackageSize(value.TotalBytes)}");
            var result = await new LocalModelDownloadService(App.LocalModelStore).DownloadAsync(
                descriptor, progress, _localModelDownloadCancellation.Token);
            if (result.Status != LocalModelDownloadStatus.Success)
            {
                LocalModelStatus = "下载未完成：" + (string.IsNullOrWhiteSpace(result.Message) ? result.Status.ToString() : result.Message);
                return;
            }
            RefreshLocalModels();
            if (result.InstalledModel is not null)
            {
                UseInstalledLocalModel(result.InstalledModel);
                if (!TrySave())
                {
                    LocalModelStatus = $"模型已安装，但自动保存配置失败，请点击设置页保存：{descriptor.DisplayName}";
                    return;
                }
            }
            LocalModelStatus = $"已安装：{descriptor.DisplayName}";
        }
        catch (Exception exception)
        {
            LocalModelStatus = "下载失败：" + exception.Message;
        }
        finally
        {
            _localModelDownloadCancellation?.Dispose();
            _localModelDownloadCancellation = null;
            IsLocalModelDownloading = false;
        }
    }

    [RelayCommand]
    private void CancelLocalModelDownload() => _localModelDownloadCancellation?.Cancel();

    [RelayCommand]
    private void OpenLocalModelDownloadLink(LocalModelDescriptor? descriptor)
    {
        if (descriptor is null || !LocalModelCatalogService.IsSafeDirectDownloadLink(descriptor.DownloadUrl))
        {
            LocalModelStatus = "下载链接无效或不是 HTTPS 地址。";
            return;
        }
        Process.Start(new ProcessStartInfo(descriptor.DownloadUrl) { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task ImportLocalModelAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "GGUF 模型 (*.gguf)|*.gguf",
            Title = "导入本地 GGUF 模型",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            LocalModelStatus = "正在校验并导入模型…";
            var model = await App.LocalModelStore.ImportModelAsync(dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName));
            RefreshLocalModels();
            LocalModelStatus = $"已导入：{model.DisplayName}";
        }
        catch (Exception exception)
        {
            LocalModelStatus = "导入失败：" + exception.Message;
        }
    }

    [RelayCommand]
    private void UseInstalledLocalModel(InstalledLocalModel? model)
    {
        if (model is null) return;
        var profile = ProviderProfiles.FirstOrDefault(item =>
            item.Platform == ProviderPlatform.ManagedLocal && item.LocalModelInstallationId == model.InstallationId);
        if (profile is null)
        {
            var id = "managed-local-" + model.InstallationId.Replace('@', '-');
            profile = new ProviderProfile
            {
                Id = id,
                Name = model.DisplayName,
                Type = ProviderType.Local,
                Platform = ProviderPlatform.ManagedLocal,
                Protocol = ProviderProtocol.OpenAICompatible,
                ApiBase = "http://127.0.0.1:0/v1",
                Model = model.InstallationId,
                SecretId = "provider-" + id,
                LocalModelInstallationId = model.InstallationId,
                LocalRuntimeProfileId = "balanced"
            };
            ProviderProfiles.Add(profile);
        }
        SetActiveProvider(profile);
        HasChanges = true;
        LocalModelStatus = $"已将“{model.DisplayName}”设为当前模型。生成时按需启动本地运行时。";
        OnPropertyChanged(nameof(FilteredProviderProfiles));
    }

    [RelayCommand]
    private void RemoveInstalledLocalModel(InstalledLocalModel? model)
    {
        if (model is null) return;
        var active = ProviderProfiles.FirstOrDefault(item => item.Id == _activeProviderProfileId);
        try
        {
            App.LocalModelStore.RemoveModel(model.InstallationId, active?.LocalModelInstallationId);
            RefreshLocalModels();
        }
        catch (Exception exception)
        {
            LocalModelStatus = "无法删除：" + exception.Message;
        }
    }

    /// <summary>
    /// Notifies the list page that a profile edited through a direct object
    /// binding has changed. ProviderProfile remains a serialization-friendly
    /// POCO, so the editor calls this at the point of user input.
    /// </summary>
    public void NotifyProviderProfileEdited()
    {
        HasChanges = true;
        OnPropertyChanged(nameof(FilteredProviderProfiles));
        OnPropertyChanged(nameof(CanDuplicateOrEditProvider));
        OnPropertyChanged(nameof(CanRemoveProvider));
    }
    public ProviderPlatformOption? SelectedProviderPlatform
    {
        get => SelectedProviderProfile is null ? null : ProviderPlatformCatalog.Get(SelectedProviderProfile.Platform);
        set
        {
            if (SelectedProviderProfile is null || value is null || SelectedProviderProfile.Platform == value.Platform) return;
            var previousSecretId = SelectedProviderProfile.SecretId;
            _apiKey = string.Empty;
            _providerConnectionChanged = true;
            ResetConnectionVerification();
            ProviderPlatformCatalog.ApplyPreset(SelectedProviderProfile, value.Platform);
            // A provider switch is an authorization-boundary change even when
            // the profile id stays the same. Do not let the previous provider's
            // key follow the new endpoint; the user must explicitly supply it.
            RebindCredentialSlot(SelectedProviderProfile, previousSecretId, forceEndpointBinding: true);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsApiBaseEditable));
            OnPropertyChanged(nameof(SelectedOpenAiProtocol));
            OnPropertyChanged(nameof(IsOpenAiProtocolSelectorVisible));
            OnPropertyChanged(nameof(SelectedProviderProfile));
            OnPropertyChanged(nameof(AvailableModels));
            OnPropertyChanged(nameof(IsOpenRouterModelRefreshVisible));
            OnPropertyChanged(nameof(SelectedModel));
            OnPropertyChanged(nameof(SelectedInferenceDescription));
            OnPropertyChanged(nameof(SelectedProviderCapabilitySummary));
            OnPropertyChanged(nameof(SelectedProviderHelp));
            OnPropertyChanged(nameof(SelectedProviderWebsite));
            OnPropertyChanged(nameof(SelectedProviderApiKeyUrl));
            OnPropertyChanged(nameof(ApiKey));
            OnPropertyChanged(nameof(HasStoredApiKey));
            OnPropertyChanged(nameof(ApiKeyStateText));
            OnPropertyChanged(nameof(ApiKeyStatusKind));
            OnPropertyChanged(nameof(ModelId));
            OnPropertyChanged(nameof(SelectedProviderCapabilitySummary));
            OnPropertyChanged(nameof(ApiBaseInput));
            OnPropertyChanged(nameof(ShowLegacyModelMapping));
            OnPropertyChanged(nameof(ProviderVerificationText));
            OnPropertyChanged(nameof(FilteredProviderProfiles));
            HasChanges = true;
            ConnectionStatus = $"已切换到 {value.DisplayName}，请填写 API Key 并测试连接";
            ConnectionStatusKind = ConnectionStatusKind.Neutral;
        }
    }
    public bool IsApiBaseEditable => SelectedProviderProfile?.Platform == ProviderPlatform.CustomOpenAICompatible;

    public ProviderProtocol SelectedOpenAiProtocol
    {
        get => SelectedProviderProfile?.Protocol ?? ProviderProtocol.OpenAICompatible;
        set
        {
            if (SelectedProviderProfile?.Platform != ProviderPlatform.OpenAI || SelectedProviderProfile.Protocol == value) return;
            if (value is not (ProviderProtocol.OpenAICompatible or ProviderProtocol.OpenAIResponses)) return;
            SelectedProviderProfile.Protocol = value;
            HasChanges = true;
            _providerConnectionChanged = true;
            ResetConnectionVerification();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedProviderCapabilitySummary));
            NotifyProviderProfileEdited();
        }
    }

    public bool IsOpenAiProtocolSelectorVisible => SelectedProviderProfile?.Platform == ProviderPlatform.OpenAI;

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
            OnPropertyChanged(nameof(SelectedInferenceDescription));
            OnPropertyChanged(nameof(SelectedProviderCapabilitySummary));
            OnPropertyChanged(nameof(FilteredProviderProfiles));
        }
    }

    public string ApiBaseInput
    {
        get => SelectedProviderProfile?.ApiBase ?? string.Empty;
        set
        {
            if (SelectedProviderProfile is null || SelectedProviderProfile.ApiBase == value) return;
            var previousSecretId = SelectedProviderProfile.SecretId;
            SelectedProviderProfile.ApiBase = value;
            // An endpoint change is a new authorization boundary. Existing
            // credentials must not follow the destination silently.
            RebindCredentialSlot(SelectedProviderProfile, previousSecretId);
            _apiKey = string.Empty;
            try
            {
                var host = Uri.TryCreate(value, UriKind.Absolute, out var endpoint) ? endpoint.IdnHost : "invalid";
                App.SecurityEventLog.Append("EndpointChanged", SelectedProviderProfile.Id, "newHost=" + host);
            }
            catch { }
            HasChanges = true;
            _providerConnectionChanged = true;
            ResetConnectionVerification();
            OnPropertyChanged(nameof(ApiKey));
            OnPropertyChanged(nameof(HasStoredApiKey));
            OnPropertyChanged(nameof(ApiKeyStateText));
            OnPropertyChanged(nameof(ApiKeyStatusKind));
            OnPropertyChanged(nameof(SelectedInferenceDescription));
            OnPropertyChanged();
        }
    }

    public InferenceLevel SelectedInferenceLevel
    {
        get => SelectedProviderProfile?.InferenceLevel ?? InferenceLevel.Medium;
        set
        {
            if (SelectedProviderProfile is null || SelectedProviderProfile.InferenceLevel == value) return;
            ProviderInferencePresets.Apply(SelectedProviderProfile, value);
            HasChanges = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustomInference));
            OnPropertyChanged(nameof(SelectedInferenceDescription));
            OnPropertyChanged(nameof(SelectedProviderCapabilitySummary));
            OnPropertyChanged(nameof(SelectedProviderProfile));
        }
    }

    public bool IsCustomInference => SelectedInferenceLevel == InferenceLevel.Custom;
    public string SelectedInferenceDescription => ProviderInferencePresets.Describe(SelectedInferenceLevel, SelectedProviderProfile);

    public string SelectedProviderCapabilitySummary
    {
        get
        {
            if (SelectedProviderProfile is not { } profile) return string.Empty;
            var resolvedModel = ProviderCapabilityResolver.ResolveModelId(profile, ModelMappingEntries);
            return ProviderCapabilityResolver.Describe(profile, resolvedModel);
        }
    }

    public bool ShowLegacyModelMapping => SelectedProviderProfile?.EnableModelMapping == true;
    public string ProviderVerificationText => SelectedProviderPlatform is { } option
        ? $"预设校验于 {option.VerifiedOn} · 模型 ID 可直接修改"
        : string.Empty;

    public bool IsOpenRouterModelRefreshVisible => SelectedProviderProfile?.Platform == ProviderPlatform.OpenRouter;
    public string OpenRouterModelCatalogStatus
    {
        get => _openRouterModelCatalogStatus;
        private set => SetProperty(ref _openRouterModelCatalogStatus, value);
    }

    [RelayCommand]
    private async Task RefreshOpenRouterModelsAsync()
    {
        if (SelectedProviderProfile?.Platform != ProviderPlatform.OpenRouter)
        {
            OpenRouterModelCatalogStatus = "请先选择 OpenRouter 配置。";
            return;
        }

        var apiKey = ResolveSelectedApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            OpenRouterModelCatalogStatus = "请先填写 OpenRouter API Key，再刷新型号目录。";
            return;
        }

        IsRefreshingOpenRouterModels = true;
        OpenRouterModelCatalogStatus = "正在从 OpenRouter 官方目录读取文本输入/输出型号…";
        try
        {
            var models = await _openRouterModelCatalogService.FetchAsync(apiKey, CancellationToken.None);
            _openRouterModelCatalog = models;
            _hasLoadedOpenRouterModelCatalog = true;
            if (SelectedProviderProfile is { Platform: ProviderPlatform.OpenRouter } profile)
            {
                var selectedModel = models.FirstOrDefault(model =>
                    string.Equals(model.ModelId, profile.Model, StringComparison.OrdinalIgnoreCase));
                if (UpdateOpenRouterCapabilitySnapshot(profile, selectedModel))
                    HasChanges = true;
            }
            OnPropertyChanged(nameof(AvailableModels));
            OnPropertyChanged(nameof(SelectedProviderCapabilitySummary));
            OpenRouterModelCatalogStatus = models.Count == 0
                ? "目录已刷新，但没有发现可用于文本输入/输出的型号。"
                : $"已刷新 {models.Count} 个支持文本输入/输出的型号及目录参数元数据；该元数据是候选提示，原生 Schema 请求将要求路由到支持全部参数的端点。";
        }
        catch (HttpRequestException exception)
        {
            OpenRouterModelCatalogStatus = exception.StatusCode is { } statusCode
                ? $"刷新失败（HTTP {(int)statusCode}）；现有目录和当前模型保持不变。"
                : "刷新失败（网络不可用）；现有目录和当前模型保持不变。";
        }
        catch (InvalidDataException)
        {
            OpenRouterModelCatalogStatus = "刷新失败（目录响应格式无效）；现有目录和当前模型保持不变。";
        }
        catch (ArgumentException)
        {
            OpenRouterModelCatalogStatus = "刷新失败（API Key 格式无效）；现有目录和当前模型保持不变。";
        }
        catch (OperationCanceledException)
        {
            OpenRouterModelCatalogStatus = "目录刷新超时；现有目录和当前模型保持不变。";
        }
        finally
        {
            IsRefreshingOpenRouterModels = false;
        }
    }

    /// <summary>当前供应商可选模型列表（显示名称 → 实际 Model ID）。</summary>
    public IReadOnlyList<ModelDefinition> AvailableModels
    {
        get
        {
            var option = SelectedProviderPlatform;
            if (option is null) return [];
            if (option.Platform == ProviderPlatform.OpenRouter && _hasLoadedOpenRouterModelCatalog)
            {
                var dynamicModels = _openRouterModelCatalog.ToList();
                var configuredModel = SelectedProviderProfile?.Model?.Trim();
                if (!string.IsNullOrWhiteSpace(configuredModel) &&
                    !dynamicModels.Any(model => string.Equals(model.ModelId, configuredModel, StringComparison.OrdinalIgnoreCase)))
                    dynamicModels.Add(new ModelDefinition("当前配置（目录未列出）", configuredModel));
                return dynamicModels;
            }
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
            if (SelectedProviderProfile.Platform == ProviderPlatform.OpenRouter)
                UpdateOpenRouterCapabilitySnapshot(SelectedProviderProfile, value);
            _providerConnectionChanged = true;
            ResetConnectionVerification();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedProviderProfile));
            OnPropertyChanged(nameof(SelectedInferenceDescription));
            OnPropertyChanged(nameof(SelectedProviderCapabilitySummary));
            OnPropertyChanged(nameof(AvailableModels));
            HasChanges = true;
        }
    }

    private static bool UpdateOpenRouterCapabilitySnapshot(ProviderProfile profile, ModelDefinition? model)
    {
        var modelId = model?.SupportedParameters is null ? string.Empty : model.ModelId;
        var supportedParameters = model?.SupportedParameters?
            .OrderBy(parameter => parameter, StringComparer.Ordinal)
            .ToList();
        if (string.Equals(profile.OpenRouterCapabilitiesModelId, modelId, StringComparison.Ordinal) &&
            ((profile.OpenRouterSupportedParameters is null && supportedParameters is null) ||
             (profile.OpenRouterSupportedParameters is not null && supportedParameters is not null &&
              profile.OpenRouterSupportedParameters.SequenceEqual(supportedParameters, StringComparer.Ordinal))))
            return false;

        profile.OpenRouterCapabilitiesModelId = modelId;
        profile.OpenRouterSupportedParameters = supportedParameters;
        return true;
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
            OnPropertyChanged(nameof(SelectedProviderCapabilitySummary));
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

    private void ModelMappingEntries_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (ModelMappingEntry entry in e.OldItems)
                entry.PropertyChanged -= ModelMappingEntry_OnPropertyChanged;
        if (e.NewItems is not null)
            foreach (ModelMappingEntry entry in e.NewItems)
                entry.PropertyChanged += ModelMappingEntry_OnPropertyChanged;
        OnPropertyChanged(nameof(SelectedProviderCapabilitySummary));
    }

    private void ModelMappingEntry_OnPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        OnPropertyChanged(nameof(SelectedProviderCapabilitySummary));
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
        ? "检测到旧版知识数据。话匣子不会读取或注入这些内容；你可以导出备份或明确永久删除。"
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
    [ObservableProperty] private string _skillDisplayName = string.Empty;
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
        SkillDisplayName = value?.EffectiveDisplayName ?? string.Empty;
        OnPropertyChanged(nameof(HasSelectedAgentSkill));
        OnPropertyChanged(nameof(IsSkillDetailsVisible));
    }
    partial void OnIsSkillEditorOpenChanged(bool value) => OnPropertyChanged(nameof(IsSkillDetailsVisible));
    partial void OnPendingAgentSkillChanged(AgentSkillCandidate? value) => OnPropertyChanged(nameof(HasPendingAgentSkill));

    public SettingsViewModel(
        ArchiveService? archiveService = null,
        ISecretStore? secretStore = null,
        Func<ProviderProfile, string?, CancellationToken, Task<ConnectionTestResult>>? connectionTester = null,
        Func<IReadOnlyList<AgentSkillRecord>>? skillCatalogLoader = null,
        OpenRouterModelCatalogService? openRouterModelCatalogService = null)
    {
        ModelMappingEntries.CollectionChanged += ModelMappingEntries_OnCollectionChanged;
        _archiveService = archiveService ?? App.ArchiveService;
        _secretStore = secretStore ?? App.SecretStore;
        _openRouterModelCatalogService = openRouterModelCatalogService ?? new OpenRouterModelCatalogService();
        _connectionTester = connectionTester ?? TestConnectionWithServiceAsync;
        _skillCatalogLoader = skillCatalogLoader;
        ProviderPlatformView = new ListCollectionView(ProviderPlatformCatalog.Options.ToList());
        ProviderPlatformView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProviderPlatformOption.TierDisplayName)));
        _originalDisplaySettings = App.Settings.Clone();
        ReloadSkinOptions();
        LoadFromSettings();
        RefreshLocalModels();
        _ = RefreshLocalModelCatalogAsync();
        LoadArchive();
        LoadHealthReport(App.LatestHealthReport);
    }

    private void ReloadSkinOptions()
    {
        SpecialSkins.Clear();
        SpecialSkins.Add(new SkinChoiceOption("default", "默认外观", null, SkinPreviewPalette.Neutral));
        foreach (var skin in App.SkinService.AvailableSkins
                     .Where(skin => !string.Equals(skin.Id, "LightPaper", StringComparison.OrdinalIgnoreCase) &&
                                    !string.Equals(skin.Id, "DarkNocturne", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(skin => skin.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            SpecialSkins.Add(new SkinChoiceOption(
                skin.Id,
                skin.DisplayName,
                ResolveSkinPreviewSource(skin),
                SkinPreviewPalette.ForSkin(skin.Id)));
        }
    }

    private static string? ResolveSkinPreviewSource(SkinManifest skin)
    {
        if (string.IsNullOrWhiteSpace(skin.PreviewPath)) return null;
        if (skin.IsBuiltIn)
            return $"pack://application:,,,/Huaxiazi;component/{skin.PreviewPath.TrimStart('/')}";
        if (string.IsNullOrWhiteSpace(skin.InstallPath)) return null;
        var installRoot = Path.GetFullPath(skin.InstallPath)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(installRoot, skin.PreviewPath));
        return candidate.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate)
            ? candidate
            : null;
    }

    partial void OnSelectedSectionChanged(string value)
    {
        // “专业能力”管理统一从“表达与生成”概览的“管理能力”入口进入，避免重复入口。
        if (value == "表达与生成") ExpressionAbilityPane = ExpressionAbilityPane.Overview;
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
    private void SaveSkillDisplayName()
    {
        if (SelectedAgentSkill is null || !EnsureStrategiesLoaded()) return;
        try
        {
            _agentSkillPackages!.RenameDisplayName(SelectedAgentSkill.Id, SkillDisplayName);
            var selectedName = SelectedAgentSkill.Id;
            ReloadStrategyItems(selectedName);
            StrategyStatus = "显示名称已更新；运行时仍使用同一项能力。";
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
                ? "尚未安装扩展能力；话匣子默认表达仍可正常使用。"
                : "已加载表达能力。启停状态会在下一次处理时生效。";
        }
        catch (Exception exception)
        {
            var detail = string.IsNullOrWhiteSpace(exception.Message) ? "本地能力文件未能完整读取。" : exception.Message;
            StrategyStatus = AgentSkillItems.Count > 0
                ? $"已保留 {AgentSkillItems.Count} 项能力；话匣子默认表达仍可用。"
                : $"能力暂不可用，话匣子默认表达仍可用：{detail}";
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
        var profile = new ProviderProfile { Id = id, Name = "新模型" };
        profile.SecretId = ProviderCredentialBinding.ForProfile(profile);
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
        duplicate.SecretId = ProviderCredentialBinding.ForProfile(duplicate);
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

    /// <summary>配置列表中的可选连通性检查；保存和退出不再隐式触发网络请求。</summary>
    [RelayCommand]
    private async Task TestProviderProfile(ProviderProfile? profile)
    {
        if (profile is null) return;
        SelectedProviderProfile = profile;
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
        if (profile.Platform == ProviderPlatform.ManagedLocal)
        {
            var installed = App.LocalModelStore.GetInstalledModels()
                .Any(model => model.InstallationId == profile.LocalModelInstallationId);
            if (!installed)
                return new ConnectionTestResult(ConnectionTestStatus.ModelUnavailable, "尚未安装所选本地模型。", null, 0, "managed-local: model-not-installed");
            return LocalRuntimeManager.IsRuntimeAvailable(LocalRuntimePaths.GetRuntimeRoot())
                ? new ConnectionTestResult(ConnectionTestStatus.Success, "本地模型与推理运行时可用。", null, 0, "managed-local: ready")
                : new ConnectionTestResult(ConnectionTestStatus.ModelUnavailable, "本地推理运行时未安装，当前无法生成。", null, 0, "managed-local: runtime-not-installed");
        }
        using var service = new AIService(profile, apiKey);
        return await service.TestConnectionAsync(cancellationToken);
    }

    private static IReadOnlyList<string> BuildSections()
    {
        return ["表达与生成", "模型连接", "本地模型", "外观与窗口", "快捷键", "历史与留存", "数据维护", "关于与更新"];
    }

    private string? ResolveSelectedApiKey()
    {
        if (SelectedProviderProfile is null) return null;
        if (_pendingApiKeys.TryGetValue(SelectedProviderProfile.SecretId, out var pending)) return pending;
        return _secretStore.Read(SelectedProviderProfile.SecretId);
    }

    /// <summary>
    /// 仅供 API Key 编辑器初始化时读取。不会写入 ApiKey 草稿、配置文件、日志或诊断信息；
    /// 显示仍由 PasswordBox 掩码控制，用户点击眼睛后才会看到明文。
    /// </summary>
    internal string GetApiKeyForEditor()
    {
        if (SelectedProviderProfile is null) return string.Empty;
        if (_pendingApiKeys.TryGetValue(SelectedProviderProfile.SecretId, out var pending))
            return pending ?? string.Empty;
        return _secretStore.Read(SelectedProviderProfile.SecretId) ?? string.Empty;
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
        // Exit-save is deliberately non-blocking. An optional list test may still be in flight;
        // its result can update the status card later without holding the settings window open.
        while (!saveWithoutVerification && IsTestingConnection)
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

    private void RebindCredentialSlot(ProviderProfile profile, string previousSecretId, bool forceEndpointBinding = false)
    {
        profile.SecretId = forceEndpointBinding
            ? ProviderCredentialBinding.ForProviderSwitch(profile)
            : ProviderCredentialBinding.ForProfile(profile);
        if (string.Equals(previousSecretId, profile.SecretId, StringComparison.Ordinal))
        {
            _pendingApiKeys[profile.SecretId] = null;
            return;
        }

        _removedSecretIds.Add(previousSecretId);
        _pendingApiKeys.Remove(previousSecretId);
        _pendingApiKeys[profile.SecretId] = null;
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
        var path = Path.Combine(App.DataRoot, "backups", $"Huaxiazi-data-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
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
        try { App.SecurityEventLog.Append("BackupRestored", Path.GetFileName(path), "backup restored without config.json"); } catch { }
        LoadArchive();
        DataStatus = "备份资料已恢复；模型端点、密钥绑定和表达策略未被覆盖，请重新打开设置确认。";
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
            HealthSummary = "健康检查仅可在话匣子应用内运行。";
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
        settings.LocalGenerationDiagnosticsEnabled = LocalGenerationDiagnosticsEnabled;
        settings.HistoryRetentionDays = HistoryRetentionDays;
        settings.SaveOriginalText = SaveOriginalText;
        settings.SaveOptimizedText = SaveOptimizedText;
        settings.IncognitoMode = IncognitoMode;
        settings.ProviderRoutingMode = ProviderRoutingMode;
        settings.PolishProviderProfileId = PolishProviderProfileId;
        settings.PromptOptimizeProviderProfileId = PromptOptimizeProviderProfileId;
        settings.FallbackProviderProfileId = FallbackProviderProfileId;
        settings.PolishFastProviderProfileId = PolishFastProviderProfileId;
        settings.PolishBalancedProviderProfileId = PolishBalancedProviderProfileId;
        settings.PolishReasoningProviderProfileId = PolishReasoningProviderProfileId;
        settings.PromptOptimizeFastProviderProfileId = PromptOptimizeFastProviderProfileId;
        settings.PromptOptimizeBalancedProviderProfileId = PromptOptimizeBalancedProviderProfileId;
        settings.PromptOptimizeReasoningProviderProfileId = PromptOptimizeReasoningProviderProfileId;
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
        settings.ShareConfirmedPreferencesWithCloud = ShareConfirmedPreferencesWithCloud;
        StoreExpressionPreferenceEditor();
        settings.ExpressionPreferenceProfile ??= new ExpressionPreferenceProfile();
        settings.ExpressionPreferenceProfile.Normalize();
        settings.ExpressionPreferenceProfile.TaskPreferences = _expressionPreferenceDrafts
            .ToDictionary(pair => pair.Key, pair => ClonePreferenceSet(pair.Value), StringComparer.Ordinal);
        StoreOutputStyleOverrideEditor();
        settings.OutputStyleOverrides = OutputStylePreferenceResolver.NormalizeOverrides(_outputStyleOverrideDrafts);
        foreach (var key in _editedExpressionPreferenceTasks)
        {
            if (!settings.ExpressionPreferenceProfile.TaskPreferences.TryGetValue(key, out var preference)) continue;
            preference.UserConfirmed = true;
            preference.Source = "user-confirmed";
            preference.Confidence = 1;
            preference.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
        settings.PreserveMeaning = PreserveMeaning;
        settings.MinimalRewrite = MinimalRewrite;
        settings.ProfessionalTone = ProfessionalTone;
        settings.OptimizationPresets = OptimizationPresets.Select(preset => preset.Clone()).ToList();
        settings.ActivePresetId = SelectedOptimizationPreset?.Id ?? string.Empty;
        settings.UpdateCheckUrl = UpdateCheckUrl.Trim();
        settings.AutoCheckUpdates = AutoCheckUpdates;
        settings.LocalModelCatalogUrl = LocalModelCatalogUrl.Trim();
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
        _expressionPreferenceDrafts = settings.ExpressionPreferenceProfile.TaskPreferences
            .ToDictionary(pair => pair.Key, pair => ClonePreferenceSet(pair.Value), StringComparer.Ordinal);
        _outputStyleOverrideDrafts = OutputStylePreferenceResolver.NormalizeOverrides(settings.OutputStyleOverrides);
        LoadOutputStyleOverrideEditor(ExpressionPreferenceTask);
        _editedExpressionPreferenceTasks.Clear();
        OnPropertyChanged(nameof(PreferenceSummary));
        OnPropertyChanged(nameof(PreferenceMetadataSummary));
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
                ? "数据目录已保存，将在重启话匣子后生效；本次会话仍使用原目录。"
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
        _localGenerationDiagnosticsEnabled = settings.LocalGenerationDiagnosticsEnabled;
        _historyRetentionDays = settings.HistoryRetentionDays;
        _saveOriginalText = settings.SaveOriginalText;
        _saveOptimizedText = settings.SaveOptimizedText;
        _incognitoMode = settings.IncognitoMode;
        _providerRoutingMode = settings.ProviderRoutingMode;
        _polishProviderProfileId = settings.PolishProviderProfileId ?? string.Empty;
        _promptOptimizeProviderProfileId = settings.PromptOptimizeProviderProfileId ?? string.Empty;
        _fallbackProviderProfileId = settings.FallbackProviderProfileId ?? string.Empty;
        _polishFastProviderProfileId = settings.PolishFastProviderProfileId ?? string.Empty;
        _polishBalancedProviderProfileId = settings.PolishBalancedProviderProfileId ?? string.Empty;
        _polishReasoningProviderProfileId = settings.PolishReasoningProviderProfileId ?? string.Empty;
        _promptOptimizeFastProviderProfileId = settings.PromptOptimizeFastProviderProfileId ?? string.Empty;
        _promptOptimizeBalancedProviderProfileId = settings.PromptOptimizeBalancedProviderProfileId ?? string.Empty;
        _promptOptimizeReasoningProviderProfileId = settings.PromptOptimizeReasoningProviderProfileId ?? string.Empty;
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
        _shareConfirmedPreferencesWithCloud = settings.ShareConfirmedPreferencesWithCloud;
        settings.ExpressionPreferenceProfile ??= new ExpressionPreferenceProfile();
        settings.ExpressionPreferenceProfile.Normalize();
        _expressionPreferenceDrafts = settings.ExpressionPreferenceProfile.TaskPreferences
            .ToDictionary(pair => pair.Key, pair => ClonePreferenceSet(pair.Value), StringComparer.Ordinal);
        _editedExpressionPreferenceTasks.Clear();
        LoadExpressionPreferenceEditor(ExpressionPreferenceTask);
        _preserveMeaning = settings.PreserveMeaning;
        _minimalRewrite = settings.MinimalRewrite;
        _professionalTone = settings.ProfessionalTone;
        OptimizationPresets.Clear();
        foreach (var preset in settings.OptimizationPresets) OptimizationPresets.Add(preset.Clone());
        _selectedOptimizationPreset = OptimizationPresets.FirstOrDefault(preset => preset.Id == settings.ActivePresetId)
            ?? OptimizationPresets.FirstOrDefault();
        _updateCheckUrl = settings.UpdateCheckUrl;
        _autoCheckUpdates = settings.AutoCheckUpdates;
        _localModelCatalogUrl = settings.LocalModelCatalogUrl;
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
public sealed record SkinChoiceOption(string Value, string Name, string? PreviewSource, SkinPreviewPalette Palette);
public sealed record CompanionDriverModeOption(CompanionDriverMode Value, string Name, string Description);
