using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PromptFloat.Models;
using PromptFloat.Services;

namespace PromptFloat.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ArchiveService _archiveService;
    private readonly DataManagementService _dataManagement = new();
    private readonly AppSettings _originalDisplaySettings;
    public IReadOnlyList<string> Sections { get; } =
        ["模型与 API", "历史与会话", "界面与显示", "窗口与行为", "优化与输出", "快捷键", "数据管理", "关于与更新"];
    [ObservableProperty] private string _selectedSection = "模型与 API";
    public IReadOnlyList<PromptCategory> Categories => PromptCategoryMetadata.AllCategories;
    public IReadOnlyList<PromptDepth> Depths => PromptDepthMetadata.AllDepths;
    public IReadOnlyList<ApplicationMode> Modes { get; } = [ApplicationMode.Polish, ApplicationMode.PromptOptimize];
    public IReadOnlyList<string> OutputStyles { get; } = ["自然", "克制", "亲切", "专业", "正式", "简洁"];
    public IReadOnlyList<string> PolishScenarios { get; } = ["私人沟通", "职场沟通", "公开发布", "正式材料", "其他"];
    public IReadOnlyList<SettingOption> ThemeModes { get; } =
        [new("System", "跟随 Windows"), new("Light", "浅色"), new("Dark", "深色")];
    public IReadOnlyList<SettingOption> CloseBehaviors { get; } =
        [new("Hide", "收缩为悬浮球"), new("Tray", "隐藏到托盘"), new("Exit", "退出程序")];
    public IReadOnlyList<SettingOption> EscapeBehaviors { get; } =
        [new("Hide", "收缩为悬浮球"), new("Tray", "隐藏到托盘"), new("None", "不执行操作")];
    public IReadOnlyList<ProviderPlatformOption> ProviderPlatforms => ProviderPlatformCatalog.Options;
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
    private bool _polishEnabled;
    public bool PolishEnabled { get => _polishEnabled; set => SetDirty(ref _polishEnabled, value); }
    private bool _promptOptimizeEnabled;
    public bool PromptOptimizeEnabled { get => _promptOptimizeEnabled; set => SetDirty(ref _promptOptimizeEnabled, value); }
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
    public bool TrayEnabled { get => _trayEnabled; set => SetDirty(ref _trayEnabled, value); }
    private bool _alwaysOnTop;
    public bool AlwaysOnTop { get => _alwaysOnTop; set => SetDirty(ref _alwaysOnTop, value); }
    private bool _autoArchive;
    public bool AutoArchive { get => _autoArchive; set => SetDirty(ref _autoArchive, value); }
    private bool _historyEnabled = true;
    public bool HistoryEnabled { get => _historyEnabled; set => SetDirty(ref _historyEnabled, value); }
    private int _historyRetentionDays = 30;
    public int HistoryRetentionDays { get => _historyRetentionDays; set => SetDirty(ref _historyRetentionDays, value); }
    private bool _saveOriginalText = true;
    public bool SaveOriginalText { get => _saveOriginalText; set => SetDirty(ref _saveOriginalText, value); }
    private bool _saveOptimizedText = true;
    public bool SaveOptimizedText { get => _saveOptimizedText; set => SetDirty(ref _saveOptimizedText, value); }
    private bool _incognitoMode;
    public bool IncognitoMode { get => _incognitoMode; set => SetDirty(ref _incognitoMode, value); }
    private bool _autoCopyAfterOptimize;
    public bool AutoCopyAfterOptimize { get => _autoCopyAfterOptimize; set => SetDirty(ref _autoCopyAfterOptimize, value); }
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
    public string OutputStyle { get => _outputStyle; set => SetDirty(ref _outputStyle, value); }
    private string _customStyleInstructions = string.Empty;
    public string CustomStyleInstructions { get => _customStyleInstructions; set => SetDirty(ref _customStyleInstructions, value); }
    private string _customSystemPrompt = string.Empty;
    public string CustomSystemPrompt { get => _customSystemPrompt; set => SetDirty(ref _customSystemPrompt, value); }
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
    private double _editorFontSize = 13;
    public double EditorFontSize { get => _editorFontSize; set { SetDirty(ref _editorFontSize, value); ApplyPreview(); } }
    private double _uiScale = 1;
    public double UiScale { get => _uiScale; set { SetDirty(ref _uiScale, value); ApplyPreview(); } }
    private double _editorDefaultHeight = 120;
    public double EditorDefaultHeight { get => _editorDefaultHeight; set { SetDirty(ref _editorDefaultHeight, value); ApplyPreview(); } }
    private bool _animationsEnabled = true;
    public bool AnimationsEnabled { get => _animationsEnabled; set { SetDirty(ref _animationsEnabled, value); ApplyPreview(); } }
    private double _windowOpacity = 1;
    public double WindowOpacity { get => _windowOpacity; set { SetDirty(ref _windowOpacity, value); ApplyPreview(); } }
    private double _floatingBallOpacity = 0.92;
    public double FloatingBallOpacity { get => _floatingBallOpacity; set => SetDirty(ref _floatingBallOpacity, value); }
    private double _floatingBallSize = 40;
    public double FloatingBallSize { get => _floatingBallSize; set => SetDirty(ref _floatingBallSize, value); }
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
    public string ValidationMessage { get => _validationMessage; private set => SetProperty(ref _validationMessage, value); }
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
            if (SetProperty(ref _selectedArchiveItem, value)) OnPropertyChanged(nameof(HasSelectedArchiveItem));
        }
    }
    public bool HasSelectedArchiveItem => SelectedArchiveItem is not null;
    private string _dataStatus = string.Empty;
    public string DataStatus { get => _dataStatus; set => SetProperty(ref _dataStatus, value); }
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
    public ProviderProfile? SelectedProviderProfile
    {
        get => _selectedProviderProfile;
        set
        {
            if (SetProperty(ref _selectedProviderProfile, value))
            {
                ApiKey = value is null ? string.Empty : App.SecretStore.Read(value.SecretId) ?? string.Empty;
                OnPropertyChanged(nameof(SelectedProviderPlatform));
                OnPropertyChanged(nameof(IsApiBaseEditable));
            }
        }
    }
    public ProviderPlatformOption? SelectedProviderPlatform
    {
        get => SelectedProviderProfile is null ? null : ProviderPlatformCatalog.Get(SelectedProviderProfile.Platform);
        set
        {
            if (SelectedProviderProfile is null || value is null || SelectedProviderProfile.Platform == value.Platform) return;
            ProviderPlatformCatalog.ApplyPreset(SelectedProviderProfile, value.Platform);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsApiBaseEditable));
            OnPropertyChanged(nameof(SelectedProviderProfile));
            HasChanges = true;
            ConnectionStatus = $"已切换到 {value.DisplayName}，请填写 API Key 并测试连接";
        }
    }
    public bool IsApiBaseEditable => SelectedProviderProfile?.Platform == ProviderPlatform.CustomOpenAICompatible;
    private string _apiKey = string.Empty;
    public string ApiKey { get => _apiKey; set => SetDirty(ref _apiKey, value); }
    private string _connectionStatus = "尚未测试";
    public string ConnectionStatus { get => _connectionStatus; private set => SetProperty(ref _connectionStatus, value); }
    private string _updateCheckUrl = string.Empty;
    public string UpdateCheckUrl { get => _updateCheckUrl; set => SetDirty(ref _updateCheckUrl, value); }
    private bool _autoCheckUpdates = true;
    public bool AutoCheckUpdates { get => _autoCheckUpdates; set => SetDirty(ref _autoCheckUpdates, value); }
    private string _healthSummary = "尚未执行健康检查";
    public string HealthSummary { get => _healthSummary; private set => SetProperty(ref _healthSummary, value); }

    public SettingsViewModel(ArchiveService? archiveService = null)
    {
        _archiveService = archiveService ?? App.ArchiveService;
        _originalDisplaySettings = App.Settings.Clone();
        LoadFromSettings();
        LoadArchive();
        LoadHealthReport(App.LatestHealthReport);
    }

    public void MarkClean() => HasChanges = false;

    public void ValidationMessageForRecorder(string message) => ValidationMessage = message;

    public void RefreshSelectedApiKey()
    {
        ApiKey = SelectedProviderProfile is null
            ? string.Empty
            : App.SecretStore.Read(SelectedProviderProfile.SecretId) ?? string.Empty;
        MarkClean();
    }

    [RelayCommand]
    private void AddProvider()
    {
        var id = "profile-" + Guid.NewGuid().ToString("N")[..8];
        var profile = new ProviderProfile { Id = id, Name = "新模型", SecretId = "provider-" + id };
        ProviderProfiles.Add(profile);
        SelectedProviderProfile = profile;
        HasChanges = true;
    }

    [RelayCommand]
    private void DuplicateProvider()
    {
        if (SelectedProviderProfile is null) return;
        var duplicate = SelectedProviderProfile.Clone();
        duplicate.Id = "profile-" + Guid.NewGuid().ToString("N")[..8];
        duplicate.SecretId = "provider-" + duplicate.Id;
        duplicate.Name = string.IsNullOrWhiteSpace(duplicate.Name) ? "配置副本" : duplicate.Name + " 副本";
        ProviderProfiles.Add(duplicate);
        SelectedProviderProfile = duplicate;
        HasChanges = true;
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task TestConnectionAsync()
    {
        if (SelectedProviderProfile is null) return;
        ConnectionStatus = "正在测试…";
        try
        {
            using var service = new AIService(SelectedProviderProfile, ApiKey);
            ConnectionStatus = await service.DiagnoseAsync("只回复 OK。", "连接测试");
        }
        catch (Exception exception)
        {
            ConnectionStatus = "连接失败：" + exception.Message;
        }
    }

    [RelayCommand]
    private void RemoveProvider()
    {
        if (SelectedProviderProfile is null || ProviderProfiles.Count <= 1) return;
        App.SecretStore.Delete(SelectedProviderProfile.SecretId);
        var index = ProviderProfiles.IndexOf(SelectedProviderProfile);
        ProviderProfiles.Remove(SelectedProviderProfile);
        SelectedProviderProfile = ProviderProfiles[Math.Max(0, index - 1)];
        HasChanges = true;
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
        ThemeMode = "System";
        EditorFontSize = 13;
        UiScale = 1;
        EditorDefaultHeight = 36;
        WindowOpacity = 1;
        AnimationsEnabled = true;
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
        foreach (var profile in ProviderProfiles)
        {
            if (profile.Type == ProviderType.Cloud &&
                Uri.TryCreate(profile.ApiBase, UriKind.Absolute, out var cloudEndpoint) &&
                cloudEndpoint.Scheme != Uri.UriSchemeHttps)
            {
                ValidationMessage = $"{profile.Name}：云端模型必须使用 HTTPS 地址，不能通过明文 HTTP 发送 API Key。";
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
        settings.EnabledModes = [];
        if (PolishEnabled) settings.EnabledModes.Add(ApplicationMode.Polish);
        if (PromptOptimizeEnabled) settings.EnabledModes.Add(ApplicationMode.PromptOptimize);
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
        settings.AutosaveDelayMilliseconds = AutosaveDelayMilliseconds;
        settings.ClarificationEnabled = ClarificationEnabled;
        settings.ShowDiff = ShowDiff;
        settings.DefaultPolishScenario = DefaultPolishScenario;
        settings.UserPersona = Persona.Trim();
        settings.OutputStyle = OutputStyle;
        settings.CustomStyleInstructions = CustomStyleInstructions.Trim();
        settings.CustomSystemPrompt = CustomSystemPrompt.Trim();
        settings.PreserveMeaning = PreserveMeaning;
        settings.MinimalRewrite = MinimalRewrite;
        settings.ProfessionalTone = ProfessionalTone;
        settings.OptimizationPresets = OptimizationPresets.Select(preset => preset.Clone()).ToList();
        settings.ActivePresetId = SelectedOptimizationPreset?.Id ?? string.Empty;
        settings.UpdateCheckUrl = UpdateCheckUrl.Trim();
        settings.AutoCheckUpdates = AutoCheckUpdates;
        settings.DataDirectory = string.IsNullOrWhiteSpace(DataDirectory) ||
            string.Equals(Path.GetFullPath(DataDirectory), App.DefaultDataRoot, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : Path.GetFullPath(DataDirectory);
        settings.ThemeMode = ThemeMode;
        settings.EditorFontSize = EditorFontSize;
        settings.UiScale = UiScale;
        settings.EditorDefaultHeight = EditorDefaultHeight;
        settings.AnimationsEnabled = AnimationsEnabled;
        settings.WindowOpacity = WindowOpacity;
        settings.FloatingBallOpacity = FloatingBallOpacity;
        settings.FloatingBallSize = FloatingBallSize;
        settings.CloseBehavior = CloseBehavior;
        settings.EscapeBehavior = EscapeBehavior;
        settings.RememberWindowSize = RememberWindowSize;
        settings.RememberFloatingBallPosition = RememberFloatingBallPosition;
        settings.SnapFloatingBallToEdge = SnapFloatingBallToEdge;
        settings.ShowInTaskbar = ShowInTaskbar;
        settings.ProviderProfiles = ProviderProfiles.Select(profile => profile.Clone()).ToList();
        settings.ActiveProviderProfileId = SelectedProviderProfile?.Id ?? ProviderProfiles[0].Id;
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

        if (SelectedProviderProfile is not null && !string.IsNullOrWhiteSpace(ApiKey))
        {
            App.SecretStore.Save(SelectedProviderProfile.SecretId, ApiKey.Trim());
        }
        var active = settings.GetActiveProviderProfile();
        settings.ApiBase = active.ApiBase;
        settings.Model = active.Model;
        settings.ApiKey = string.Empty;
        try
        {
            App.ConfigService.Save(settings);
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
        if (!string.Equals(previousDataRoot, App.DataRoot, StringComparison.OrdinalIgnoreCase))
        {
            ValidationMessage = string.IsNullOrWhiteSpace(ValidationMessage)
                ? "数据目录已保存，将在重启 Vesper 后生效；本次会话仍使用原目录。"
                : ValidationMessage + "；数据目录将在重启后生效。";
        }
        ThemeService.Apply(settings);
        ((App)System.Windows.Application.Current).ApplyResidentSettings();
        StartupService.TrySetEnabled(settings.StartWithWindows, out _);

        HasChanges = false;
        return true;
    }

    [RelayCommand]
    private void Cancel()
    {
        try
        {
            if (System.Windows.Application.Current is not null)
            {
                ThemeService.Apply(_originalDisplaySettings);
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
        _polishEnabled = settings.EnabledModes.Contains(ApplicationMode.Polish);
        _promptOptimizeEnabled = settings.EnabledModes.Contains(ApplicationMode.PromptOptimize);
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
        _autosaveDelayMilliseconds = settings.AutosaveDelayMilliseconds;
        _clarificationEnabled = settings.ClarificationEnabled;
        _showDiff = settings.ShowDiff;
        _defaultPolishScenario = settings.DefaultPolishScenario;
        _persona = settings.UserPersona;
        _outputStyle = settings.OutputStyle;
        _customStyleInstructions = settings.CustomStyleInstructions;
        _customSystemPrompt = settings.CustomSystemPrompt;
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
        _editorFontSize = settings.EditorFontSize;
        _uiScale = settings.UiScale;
        _editorDefaultHeight = settings.EditorDefaultHeight;
        _animationsEnabled = settings.AnimationsEnabled;
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
        _selectedProviderProfile = ProviderProfiles.First(profile => profile.Id == settings.ActiveProviderProfileId);
        _apiKey = App.SecretStore.Read(_selectedProviderProfile.SecretId) ?? string.Empty;
        HasChanges = false;
    }

    private void SetDirty<T>(ref T field, T value)
    {
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
            preview.EditorFontSize = _editorFontSize;
            preview.UiScale = _uiScale;
            preview.EditorDefaultHeight = _editorDefaultHeight;
            preview.AnimationsEnabled = _animationsEnabled;
            preview.WindowOpacity = _windowOpacity;
            preview.NormalizeDisplaySettings();
            if (System.Windows.Application.Current is not null)
            {
                ThemeService.Apply(preview);
                if (System.Windows.Application.Current is App app) app.ApplyDisplaySettings(preview);
            }
        }
        catch { }
    }

    private bool TryParseArchiveDates(out DateTimeOffset? from, out DateTimeOffset? to)
    {
        from = null;
        to = null;
        if (!string.IsNullOrWhiteSpace(ArchiveFromText))
        {
            if (!DateTime.TryParse(ArchiveFromText, out var value))
            {
                DataStatus = "开始日期格式无效，请使用 yyyy-MM-dd。";
                return false;
            }
            from = new DateTimeOffset(value.Date);
        }
        if (!string.IsNullOrWhiteSpace(ArchiveToText))
        {
            if (!DateTime.TryParse(ArchiveToText, out var value))
            {
                DataStatus = "结束日期格式无效，请使用 yyyy-MM-dd。";
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
