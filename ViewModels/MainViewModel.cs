using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Huaxiazi.Models;
using Huaxiazi.Services;

namespace Huaxiazi.ViewModels;

/// <summary>一次表达任务在悬浮球上的可见工作阶段。</summary>
public enum CompanionWorkPhase
{
    Ready,
    Starting,
    Processing
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly WorkspaceDraftService _draftStore;
    private readonly ArchiveService _archiveService;
    private readonly IClipboardService _clipboardService;
    private readonly Func<ProviderProfile, string?, ApplicationMode, GenerationDiagnosticsScope?, ITextGenerationClient> _generationClientFactory;
    // 撤回/重做历史按模式（润色 / 提示词优化）拆分，避免切换模式污染彼此的操作栈
    private readonly Dictionary<ApplicationMode, Stack<WorkspaceDraft>> _undoByMode = new();
    private readonly Dictionary<ApplicationMode, Stack<WorkspaceDraft>> _redoByMode = new();
    private bool _restoringWorkspace;
    private bool _draftDirty;
    private CancellationTokenSource? _requestCancellation;
    private long _requestVersion;
    private CancellationTokenSource? _undoCancellation;
    private ContentRevision? _lastSavedRevision;
    private Guid? _currentItemId;
    private string _currentScenario = "其他";
    private string _currentTopic = "未命名表达";
    private ApplicationMode _preferenceSignalTask = ApplicationMode.Polish;
    private string _preferenceSignalScenario = string.Empty;
    private string _preferenceSignalOutputStyle = "自然";
    private readonly ExpressionPreferenceFeedbackSession _preferenceFeedbackSession = new();
    private readonly SmartContextAnalyzer _contextAnalyzer = new();
    private readonly StructuredPreferenceService _preferenceService = new();
    private readonly ProfessionalizationPlanner _professionalizationPlanner = new();
    private string _generatedResultBeforeEdit = string.Empty;
    private SourceApplicationContext? _sourceApplicationContext;
    private string? _clarificationSubmission;
    private string? _clarificationOriginalInput;
    private AssistantEmotionHint? _assistantEmotion;
    private CancellationTokenSource? _assistantEmotionCancellation;
    private GenerationFailureException? _lastGenerationFailure;
    private string _lastGenerationProfileId = string.Empty;
    private string _lastGenerationModelName = string.Empty;
    private bool _companionCompleted;
    public GenerationFailureException? LastGenerationFailure
    {
        get => _lastGenerationFailure;
        private set => SetProperty(ref _lastGenerationFailure, value);
    }

    public IReadOnlyList<PromptCategory> Categories => App.Settings.EnabledPromptCategories;
    public IReadOnlyList<PromptDepth> Depths => PromptDepthMetadata.AllDepths;
    public IReadOnlyList<string> History => App.Settings.History;
    public IReadOnlyList<ApplicationMode> EnabledModes => App.Settings.EnabledModes;
    public IReadOnlyList<ProviderProfile> ProviderProfiles => App.Settings.ProviderProfiles;
    public IReadOnlyList<OptimizationPreset> Presets => App.Settings.OptimizationPresets;
    public ObservableCollection<ContentRevision> RecentRevisions { get; } = [];
    public bool ShowModeSwitcher => App.Settings.EnabledModes.Count > 1;
    public bool IsPolishMode => CurrentMode == ApplicationMode.Polish;
    public bool IsPromptOptimizeMode => CurrentMode == ApplicationMode.PromptOptimize;
    public bool CanUndoWorkspace => UndoStack.Count > 0;
    public bool CanRedoWorkspace => RedoStack.Count > 0;
    public bool CanRegenerate => !string.IsNullOrWhiteSpace(UserInput) && !IsBusy;

    /// <summary>模式指示器当前显示的工作模式名称。</summary>
    public string ModeToggleLabel => CurrentMode == ApplicationMode.Polish ? "润色" : "提示词";

    /// <summary>单按钮模式轮播：点击后要切换到的目标模式。</summary>
    public ApplicationMode ToggleModeTarget => CurrentMode == ApplicationMode.Polish ? ApplicationMode.PromptOptimize : ApplicationMode.Polish;

    /// <summary>内嵌视图切换：显示"点击后切换到的视图"名称（原文/优化稿）。</summary>
    public string ViewToggleLabel => ViewMode == ViewMode.Original ? "优化稿" : "原文";

    /// <summary>内嵌视图切换：点击后要切换到的目标视图。</summary>
    public ViewMode ViewToggleTarget => ViewMode == ViewMode.Original ? ViewMode.Optimized : ViewMode.Original;
    public string ActiveProviderLabel
    {
        get
        {
            var profile = App.Settings.GetActiveProviderProfile();
            return $"{profile.Name} · {profile.Model}";
        }
    }

    public string LastUsedProviderLabel { get; private set; } = string.Empty;
    public string LastUsedOutputStyleLabel { get; private set; } = string.Empty;
    public ProviderProfile ActiveProviderProfile
    {
        get => App.Settings.GetActiveProviderProfile();
        set => SelectProvider(value);
    }
    private OptimizationPreset? _selectedPreset;
    public OptimizationPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetProperty(ref _selectedPreset, value) || value is null) return;
            RecordWorkspaceUndo();
            App.Settings.ActivePresetId = value.Id;
            if (App.Settings.EnabledModes.Contains(value.Mode)) CurrentMode = value.Mode;
            if (App.Settings.EnabledPromptCategories.Contains(value.Category)) SelectedCategory = value.Category;
            SelectedDepth = value.Depth;
            // A preset is a named shortcut for the complete expression configuration. Keep the
            // global defaults as the single runtime source so polish and prompt optimization
            // consume the same style instructions after a preset is selected.
            App.Settings.OutputStyle = value.OutputStyle;
            App.Settings.CustomStyleInstructions = value.Instructions;
            App.Settings.CustomSystemPrompt = value.CustomSystemPrompt;
            MarkDraftDirty();
        }
    }

    private string _userInput = string.Empty;
    private bool _clipboardPrefilled;
    public string UserInput
    {
        get => _userInput;
        set
        {
            if (SetProperty(ref _userInput, value))
            {
                // Changing the source text starts a new task. Do not let an abandoned
                // clarification panel carry its previous answer or original-text anchor
                // into the next request. An active submission is deliberately preserved
                // so retry-after-failure can resubmit the same clarification context.
                if (!_restoringWorkspace && !IsBusy && string.IsNullOrWhiteSpace(_clarificationSubmission))
                {
                    var hadClarificationState = HasClarification || ClarificationQuestions.Count > 0 ||
                        !string.IsNullOrWhiteSpace(ClarificationAnswer) || _clarificationOriginalInput is not null;
                    ClarificationQuestions = [];
                    HasClarification = false;
                    ClarificationAnswer = string.Empty;
                    _clarificationOriginalInput = null;
                    if (hadClarificationState) ArchiveStatus = string.Empty;
                }
                _clipboardPrefilled = false;
                MarkDraftDirty();
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(IsDisplayTextEmpty));
                RegenerateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private string _optimizedResult = string.Empty;
    public string OptimizedResult
    {
        get => _optimizedResult;
        set
        {
            if (SetProperty(ref _optimizedResult, value))
            {
                MarkDraftDirty();
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(ShowResultToggle));
                OnPropertyChanged(nameof(IsDisplayTextEmpty));
            }
        }
    }

    private ViewMode _viewMode = ViewMode.Original;
    public ViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (SetProperty(ref _viewMode, value))
            {
                MarkDraftDirty();
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(IsReadOnly));
                OnPropertyChanged(nameof(IsEditorView));
                OnPropertyChanged(nameof(ShowDiff));
                OnPropertyChanged(nameof(IsDisplayTextEmpty));
                OnPropertyChanged(nameof(ViewToggleLabel));
                OnPropertyChanged(nameof(ViewToggleTarget));
            }
        }
    }

    private ApplicationMode _currentMode = ApplicationMode.Polish;
    public ApplicationMode CurrentMode
    {
        get => _currentMode;
        set
        {
            if (IsBusy && value != _currentMode) return;
            if (SetProperty(ref _currentMode, value))
            {
                MarkDraftDirty();
                OnPropertyChanged(nameof(IsPolishMode));
                OnPropertyChanged(nameof(IsPromptOptimizeMode));
                OnPropertyChanged(nameof(EditorPlaceholderText));
                OnPropertyChanged(nameof(PrimaryActionText));
                OnPropertyChanged(nameof(ModeToggleLabel));
                OnPropertyChanged(nameof(ToggleModeTarget));
                // 撤回/重做历史按模式拆分，切换模式后可用状态随之更新
                NotifyWorkspaceHistoryChanged();
            }
        }
    }

    public string DisplayText
    {
        get => ViewMode switch
        {
            ViewMode.Optimized => OptimizedResult,
            _ => UserInput
        };
        set
        {
            if (ViewMode == ViewMode.Optimized)
            {
                if (IsEditingResult) OptimizedResult = value ?? string.Empty;
            }
            else
            {
                UserInput = value ?? string.Empty;
            }
        }
    }

    public bool IsReadOnly => ViewMode == ViewMode.Optimized && !IsEditingResult;
    public bool IsDisplayTextEmpty => string.IsNullOrEmpty(DisplayText);
    public string EditorPlaceholderText => IsPolishMode
        ? "粘贴或输入要润色的内容…\n可选：补充对象、目的或语气"
        : "描述你想让 AI 完成的任务…\n可选：补充背景、限制或输出格式";
    public string PrimaryActionText => IsBusy
        ? (IsPolishMode ? "润色中…" : "优化中…")
        : (IsPolishMode ? "润色" : "优化");
    public bool ShowResultToggle => !string.IsNullOrEmpty(OptimizedResult);
    public bool IsEditorView => true;
    public CompanionVisualState CompanionState => HasError
        ? CompanionVisualState.Error
        : HasClarification
            ? CompanionVisualState.Curious
                : IsBusy
                    ? _companionWorkPhase switch
                    {
                        CompanionWorkPhase.Starting => CompanionVisualState.Working,
                        CompanionWorkPhase.Processing => CompanionVisualState.Thinking,
                        _ => CompanionVisualState.Thinking
                    }
                : _companionCompleted
                    ? CompanionVisualState.Happy
                    : _assistantEmotion is not null
                        ? CompanionEmotionMapper.ToVisualState(_assistantEmotion)
                : ArchiveStatus.StartsWith("已归档", StringComparison.Ordinal) ||
                  ArchiveStatus.StartsWith("已采用", StringComparison.Ordinal) ||
                  ArchiveStatus == "已复制"
                    ? CompanionVisualState.Happy
                    : CompanionVisualState.Idle;
    public string OperationalNotice => HasError
        ? ErrorMessage
        : HasClarification
            ? "请补充信息后继续"
            : IsBusy
                ? _companionWorkPhase == CompanionWorkPhase.Starting
                    ? (IsPolishMode ? "已开始整理表达…" : "已开始优化提示词…")
                    : (IsPolishMode ? "正在整理表达，请稍候…" : "正在优化提示词，请稍候…")
                : _companionCompleted
                    ? (IsPolishMode ? "表达润色完成" : "提示词优化完成")
                : _clipboardPrefilled
                    ? "已填入剪贴板内容，请确认后再发送"
                : ArchiveStatus;
    /// <summary>澄清面板中的实际问题；标题状态栏只显示概括提示，避免挤占标题并泄露长原文。</summary>
    public string ClarificationPrompt => string.Join("  ", ClarificationQuestions);
    public bool HasOperationalNotice => !string.IsNullOrWhiteSpace(OperationalNotice);

    private bool _showDiff;
    public bool ShowDiff
    {
        get => _showDiff && ViewMode == ViewMode.Optimized && !IsEditingResult;
        set
        {
            if (SetProperty(ref _showDiff, value)) OnPropertyChanged(nameof(ShowDiff));
        }
    }

    [ObservableProperty] private PromptCategory _selectedCategory = PromptCategory.General;
    [ObservableProperty] private PromptDepth _selectedDepth = PromptDepth.Standard;
    [ObservableProperty] private bool _isBusy;
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(PrimaryActionText));
        NotifyCompanionFeedbackChanged();
        RegenerateCommand.NotifyCanExecuteChanged();
    }
    private CompanionWorkPhase _companionWorkPhase = CompanionWorkPhase.Ready;

    public CompanionWorkPhase CompanionWorkPhase => _companionWorkPhase;

    internal void SetCompanionWorkPhase(CompanionWorkPhase phase)
    {
        if (_companionWorkPhase == phase) return;
        _companionWorkPhase = phase;
        OnPropertyChanged(nameof(CompanionWorkPhase));
        NotifyCompanionFeedbackChanged();
    }

    internal void MarkClipboardPrefilled()
    {
        _clipboardPrefilled = true;
        NotifyCompanionFeedbackChanged();
    }

    internal void SetCompanionCompletion(bool completed)
    {
        if (_companionCompleted == completed) return;
        _companionCompleted = completed;
        NotifyCompanionFeedbackChanged();
    }
    [ObservableProperty] private string _errorMessage = string.Empty;
    partial void OnErrorMessageChanged(string value) => NotifyCompanionFeedbackChanged();
    [ObservableProperty] private bool _hasError;
    partial void OnHasErrorChanged(bool value) => NotifyCompanionFeedbackChanged();
    [ObservableProperty] private bool _isContextExpanded;
    [ObservableProperty] private string _recipient = string.Empty;
    [ObservableProperty] private string _channel = string.Empty;
    [ObservableProperty] private string _purpose = string.Empty;
    [ObservableProperty] private string _formality = string.Empty;
    [ObservableProperty] private string _scenario = string.Empty;
    [ObservableProperty] private IReadOnlyList<string> _clarificationQuestions = [];
    partial void OnClarificationQuestionsChanged(IReadOnlyList<string> value) => NotifyCompanionFeedbackChanged();
    [ObservableProperty] private bool _hasClarification;
    partial void OnHasClarificationChanged(bool value) => NotifyCompanionFeedbackChanged();
    [ObservableProperty] private string _clarificationAnswer = string.Empty;
    [ObservableProperty] private string _archiveStatus = string.Empty;
    partial void OnArchiveStatusChanged(string value) => NotifyCompanionFeedbackChanged();
    [ObservableProperty] private string _draftStatus = string.Empty;
    [ObservableProperty] private bool _canUndoArchive;
    [ObservableProperty] private bool _isResultUnarchived;

    private bool _isEditingResult;
    public bool IsEditingResult
    {
        get => _isEditingResult;
        set
        {
            if (SetProperty(ref _isEditingResult, value))
            {
                OnPropertyChanged(nameof(IsReadOnly));
                OnPropertyChanged(nameof(ShowDiff));
            }
        }
    }

    private void NotifyCompanionFeedbackChanged()
    {
        OnPropertyChanged(nameof(CompanionState));
        OnPropertyChanged(nameof(OperationalNotice));
        OnPropertyChanged(nameof(ClarificationPrompt));
        OnPropertyChanged(nameof(HasOperationalNotice));
    }

    public MainViewModel() : this(new WorkspaceDraftService(App.DataRoot), new ArchiveService(App.DataRoot))
    {
    }

    internal MainViewModel(WorkspaceDraftService draftStore, ArchiveService archiveService)
        : this(draftStore, archiveService, (profile, key, task, diagnosticsScope) => App.CreateGenerationClient(profile, key, task, diagnosticsScope))
    {
    }

    internal MainViewModel(
        WorkspaceDraftService draftStore,
        ArchiveService archiveService,
        Func<ProviderProfile, string?, ITextGenerationClient> generationClientFactory)
        : this(draftStore, archiveService, (profile, key, _, _) => generationClientFactory(profile, key))
    {
    }

    internal MainViewModel(
        WorkspaceDraftService draftStore,
        ArchiveService archiveService,
        Func<ProviderProfile, string?, ApplicationMode, ITextGenerationClient> generationClientFactory)
        : this(draftStore, archiveService, (profile, key, task, _) => generationClientFactory(profile, key, task))
    {
    }

    internal MainViewModel(
        WorkspaceDraftService draftStore,
        ArchiveService archiveService,
        Func<ProviderProfile, string?, ApplicationMode, GenerationDiagnosticsScope?, ITextGenerationClient> generationClientFactory,
        IClipboardService? clipboardService = null)
    {
        _draftStore = draftStore ?? throw new ArgumentNullException(nameof(draftStore));
        _archiveService = archiveService ?? throw new ArgumentNullException(nameof(archiveService));
        _generationClientFactory = generationClientFactory ?? throw new ArgumentNullException(nameof(generationClientFactory));
        _clipboardService = clipboardService ?? App.ClipboardService;
        App.Settings.NormalizeProductModes();
        App.Settings.NormalizeProviderProfiles();
        App.Settings.NormalizePromptSettings();
        _currentMode = App.Settings.DefaultMode;
        _scenario = App.Settings.DefaultPolishScenario;
        _selectedCategory = App.Settings.GetDefaultCategory();
        _selectedDepth = App.Settings.GetDefaultDepth();
        _showDiff = App.Settings.ShowDiff;
        _selectedPreset = App.Settings.OptimizationPresets.FirstOrDefault(preset => preset.Id == App.Settings.ActivePresetId);
        if (App.Settings.IncognitoMode)
        {
            _draftStore.Clear();
        }
        else if (_draftStore.Load() is { } draft)
        {
            RestoreWorkspace(draft);
        }
        RefreshHistory();
    }

    partial void OnSelectedCategoryChanged(PromptCategory value) => MarkDraftDirty();
    partial void OnSelectedDepthChanged(PromptDepth value) => MarkDraftDirty();
    partial void OnRecipientChanged(string value) => MarkDraftDirty();
    partial void OnChannelChanged(string value) => MarkDraftDirty();
    partial void OnPurposeChanged(string value) => MarkDraftDirty();
    partial void OnFormalityChanged(string value) => MarkDraftDirty();
    partial void OnScenarioChanged(string value) => MarkDraftDirty();

    public void ReloadPreferences()
    {
        if (!CanLearnPreferences()) _preferenceFeedbackSession.Cancel();
        _showDiff = App.Settings.ShowDiff;
        OnPropertyChanged(nameof(ShowDiff));
        OnPropertyChanged(nameof(Categories));
        OnPropertyChanged(nameof(Depths));
        OnPropertyChanged(nameof(EnabledModes));
        OnPropertyChanged(nameof(ProviderProfiles));
        OnPropertyChanged(nameof(Presets));
        OnPropertyChanged(nameof(ShowModeSwitcher));
        OnPropertyChanged(nameof(ActiveProviderProfile));
        OnPropertyChanged(nameof(ActiveProviderLabel));
    }

    public void SetSourceApplicationContext(SourceApplicationContext? context) => _sourceApplicationContext = context;

    [RelayCommand]
    private void SelectMode(ApplicationMode mode)
    {
        if (IsBusy || !App.Settings.EnabledModes.Contains(mode) || mode == CurrentMode) return;
        // 模式切换本身不产生撤回快照：每个模式保留自己的操作栈，避免同一输入反复切换时历史互相污染
        CurrentMode = mode;
    }

    [RelayCommand]
    private void SelectProvider(ProviderProfile? profile)
    {
        if (profile is null || !App.Settings.ProviderProfiles.Any(item => item.Id == profile.Id)) return;
        App.Settings.ActiveProviderProfileId = profile.Id;
        MarkDraftDirty();
        OnPropertyChanged(nameof(ActiveProviderProfile));
        OnPropertyChanged(nameof(ActiveProviderLabel));
    }

    [RelayCommand] private void ToggleContext() => IsContextExpanded = !IsContextExpanded;
    [RelayCommand] private void SelectCategory(PromptCategory category) => SelectedCategory = category;
    [RelayCommand] private void SelectDepth(PromptDepth depth) => SelectedDepth = depth;

    [RelayCommand]
    private void SetViewMode(ViewMode mode)
    {
        if (mode == ViewMode.Optimized && string.IsNullOrEmpty(OptimizedResult)) return;
        ViewMode = mode;
    }

    /// <summary>历史条目点击：把历史原文载入编辑器。</summary>
    [RelayCommand]
    private void UseHistory(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        UserInput = text;
        ResetResultState();
        ViewMode = ViewMode.Original;
    }

    [RelayCommand]
    private void ClearInput()
    {
        RecordWorkspaceUndo();
        UserInput = string.Empty;
        ResetResultState();
        ErrorMessage = string.Empty;
        HasError = false;
    }

    [RelayCommand]
    private void CopyResult()
    {
        if (string.IsNullOrEmpty(OptimizedResult)) return;
        try
        {
            _clipboardService.CopyText(OptimizedResult);
            if (CanLearnPreferences() && _preferenceFeedbackSession.IsActive)
            {
                _preferenceService.RecordAcceptance(App.Settings.ExpressionPreferenceProfile, _preferenceSignalTask, _preferenceSignalScenario);
                CapturePreferenceFeedbackEdit();
                CompletePreferenceFeedback(accepted: true, rejected: false);
                SavePreferenceProfile();
            }
        }
        catch (Exception ex) { ShowError($"复制失败：{ex.Message}"); }
    }

    [RelayCommand]
    private void PasteFromClipboard()
    {
        try
        {
            var text = _clipboardService.GetText();
            if (!string.IsNullOrEmpty(text)) UserInput = text;
        }
        catch (Exception ex) { ShowError($"读取剪贴板失败：{ex.Message}"); }
    }

    [RelayCommand]
    private void MinimizeToBall() =>
        App.Current.Dispatcher.Invoke(() => ((App)App.Current).CollapseToFloatingBall());

    [RelayCommand]
    private void Cancel()
    {
        Interlocked.Increment(ref _requestVersion);
        _requestCancellation?.Cancel();
        IsBusy = false;
        ArchiveStatus = "已取消";
    }

    [RelayCommand]
    private void UndoWorkspace()
    {
        var undo = UndoStack;
        if (undo.Count == 0) return;
        var undoChangesGeneratedOutput = !string.Equals(undo.Peek().OptimizedResult, OptimizedResult, StringComparison.Ordinal);
        if (CanLearnPreferences() && _preferenceFeedbackSession.IsActive)
        {
            _preferenceService.RecordUndo(App.Settings.ExpressionPreferenceProfile, _preferenceSignalTask, _preferenceSignalScenario);
            if (undoChangesGeneratedOutput)
            {
                CapturePreferenceFeedbackEdit();
                CompletePreferenceFeedback(accepted: false, rejected: true);
            }
            SavePreferenceProfile();
        }
        RedoStack.Push(CaptureWorkspace());
        RestoreWorkspace(undo.Pop());
        NotifyWorkspaceHistoryChanged();
    }

    [RelayCommand]
    private void RedoWorkspace()
    {
        var redo = RedoStack;
        if (redo.Count == 0) return;
        UndoStack.Push(CaptureWorkspace());
        RestoreWorkspace(redo.Pop());
        NotifyWorkspaceHistoryChanged();
    }

    [RelayCommand]
    private void LoadRevision(ContentRevision? revision)
    {
        if (revision is null) return;
        if (IsBusy)
        {
            Interlocked.Increment(ref _requestVersion);
            _requestCancellation?.Cancel();
            IsBusy = false;
        }
        RecordWorkspaceUndo();
        _restoringWorkspace = true;
        try
        {
            UserInput = revision.OriginalText;
            OptimizedResult = revision.FinalText;
            CurrentMode = revision.Mode;
            var hasSavedResult = !string.IsNullOrWhiteSpace(revision.FinalText);
            ViewMode = hasSavedResult ? ViewMode.Optimized : ViewMode.Original;
            _generatedResultBeforeEdit = hasSavedResult ? revision.FinalText : string.Empty;
            IsEditingResult = hasSavedResult;
            _currentItemId = revision.ItemId;
            _currentScenario = revision.Scenario;
            _currentTopic = revision.Topic;
            _preferenceSignalTask = revision.Mode;
            _preferenceSignalScenario = revision.Mode == ApplicationMode.PromptOptimize ? revision.Topic : revision.Scenario;
            ArchiveStatus = $"已载入 {revision.Topic} · v{revision.Version:00}";
        }
        finally
        {
            _restoringWorkspace = false;
            MarkDraftDirty();
        }
    }

    [RelayCommand]
    private void RefreshHistory()
    {
        RecentRevisions.Clear();
        try
        {
            foreach (var revision in _archiveService.Search(null).Take(50)) RecentRevisions.Add(revision);
        }
        catch (Exception exception)
        {
            ArchiveStatus = "本地历史记录暂不可用；润色和提示词功能仍可继续。";
            App.LogApplicationError("ArchiveUnavailable", exception);
        }
    }

    public void SaveDraftIfDirty()
    {
        if (!_draftDirty) return;
        if (App.Settings.IncognitoMode)
        {
            _draftStore.Clear();
            _draftDirty = false;
            DraftStatus = "无痕模式：未保存草稿";
            return;
        }
        try
        {
            _draftStore.Save(CaptureWorkspace());
            _draftDirty = false;
            DraftStatus = $"已自动保存 {DateTime.Now:HH:mm}";
        }
        catch (Exception ex)
        {
            DraftStatus = $"自动保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    public async Task OptimizeAsync()
    {
        if (IsBusy) return;

        // A fresh request must establish its own clarification context. The only time
        // the previous anchor and answer are valid is while SubmitClarificationAsync
        // carries the composed submission into this call.
        if (string.IsNullOrWhiteSpace(_clarificationSubmission))
        {
            _clarificationOriginalInput = null;
            ClarificationAnswer = string.Empty;
        }

        PromoteEditedResultToNewInput();
        if (string.IsNullOrWhiteSpace(UserInput))
        {
            ShowError(CurrentMode == ApplicationMode.Polish ? "请先输入想润色的原文。" : "请先输入你的原始需求。");
            return;
        }

        HasError = false;
        ErrorMessage = string.Empty;
        LastGenerationFailure = null;
        ApplyAssistantEmotion(null);
        HasClarification = false;
        ClarificationQuestions = [];
        SetCompanionCompletion(false);
        SetCompanionWorkPhase(CompanionWorkPhase.Starting);
        IsBusy = true;
        var requestVersion = Interlocked.Increment(ref _requestVersion);
        var requestMode = CurrentMode;
        _requestCancellation?.Dispose();
        _requestCancellation = new CancellationTokenSource();
        var requestCancellation = _requestCancellation;
        GenerationDiagnosticsScope? diagnosticsScope = null;

        try
        {
            // Yield once so the user can see the distinct “开始处理” face
            // before the sustained network/thinking state takes over.
            await Task.Yield();
            if (requestVersion == Volatile.Read(ref _requestVersion) && IsBusy)
                SetCompanionWorkPhase(CompanionWorkPhase.Processing);
            var parsedInput = InputContextParser.Parse(_clarificationSubmission ?? UserInput);
            ModelTier? routingTier = null;
            string? routingReason = null;
            if (App.Settings.ProviderRoutingMode == ProviderRoutingMode.Automatic)
            {
                var routingPlan = CreateRoutingPlan(requestMode, parsedInput);
                if (routingPlan.NeedsClarification)
                {
                    if (App.Settings.ClarificationEnabled)
                    {
                        ClarificationQuestions = routingPlan.ClarificationQuestions;
                        HasClarification = true;
                        _clarificationOriginalInput ??= UserInput;
                        ArchiveStatus = "需要补充关键信息";
                    }
                    else
                    {
                        var missingInformation = MissingInformationSummary.For(routingPlan);
                        ArchiveStatus = "信息不足，未生成";
                        ShowError($"当前信息不足以完成“{FirstNonEmpty(routingPlan.Purpose, "当前任务")}”。缺少：{missingInformation}。本次未向模型发送请求。补充信息后重试，或开启“关键信息缺失时先询问”。");
                    }
                    return;
                }
                if (!Enum.TryParse<ModelTier>(routingPlan.RecommendedModelTier, ignoreCase: false, out var tier))
                    throw new InvalidOperationException("当前任务无法确定自动路由模型档位，请改用手动选择或检查任务设置。");
                routingTier = tier;
                routingReason = routingPlan.ModelSelectionReason;
            }
            var route = ProviderRouter.Select(App.Settings, requestMode, routingTier, routingReason);
            var includeConfirmedPreferences = PreferenceDisclosurePolicy.CanSendConfirmedPreferences(App.Settings, route);
            var includeLegacyPreferences = PreferenceDisclosurePolicy.CanSendLegacyPreferences(App.Settings, route);
            var profile = route.Profile;
            diagnosticsScope = App.CreateGenerationDiagnosticsScope(
                requestMode,
                App.Settings.ProviderRoutingMode,
                route.Reason,
                route.Profile.Id,
                route.ConfiguredFallback?.Id);
            LastUsedProviderLabel = $"本次尝试：{profile.Name} · {profile.Model}（{ProviderRouter.DescribeReason(route.Reason, route.ModelSelectionReason)}）";
            OnPropertyChanged(nameof(LastUsedProviderLabel));
            ITextGenerationClient CreateGenerationClient()
            {
                var key = App.SecretStore.Read(profile.SecretId);
                ITextGenerationClient generationClient = _generationClientFactory(profile, key, requestMode, diagnosticsScope);
                if (route.ConfiguredFallback is { } fallbackProfile)
                {
                    generationClient = new ProviderFallbackGenerationClient(
                        generationClient,
                        fallbackProfile,
                        fallback => _generationClientFactory(fallback, App.SecretStore.Read(fallback.SecretId), requestMode, diagnosticsScope),
                        fallback =>
                        {
                            profile = fallback;
                            LastUsedProviderLabel = $"主模型失败后使用备用：{fallback.Name} · {fallback.Model}";
                            OnPropertyChanged(nameof(LastUsedProviderLabel));
                        });
                }
                return generationClient;
            }

            if (requestMode == ApplicationMode.Polish)
            {
                var polishRequest = CreatePolishRequest(parsedInput, profile, includeConfirmedPreferences, includeLegacyPreferences);
                if (App.Settings.ClarificationEnabled && polishRequest.Professionalization is { NeedsClarification: true } polishPlan)
                {
                    ClarificationQuestions = polishPlan.ClarificationQuestions;
                    HasClarification = true;
                    _clarificationOriginalInput ??= UserInput;
                    ArchiveStatus = "需要补充关键信息";
                    return;
                }
                if (!App.Settings.ClarificationEnabled && polishRequest.Professionalization is { NeedsClarification: true } incompletePlan)
                {
                    var missingInformation = MissingInformationSummary.For(incompletePlan);
                    ArchiveStatus = "信息不足，未生成";
                    ShowError($"当前信息不足以完成“{FirstNonEmpty(incompletePlan.Purpose, "当前任务")}”。缺少：{missingInformation}。本次未向模型发送请求。补充信息后重试，或开启“关键信息缺失时先询问”。");
                    return;
                }
                var client = CreateGenerationClient();
                using var clientDisposal = client as IDisposable;
                var workflow = new PolishWorkflowService(client, App.PolishPromptBuilder, _archiveService);
                var shouldArchive = ShouldArchive();
                var result = await workflow.ExecuteAsync(
                    polishRequest,
                    App.Settings.ClarificationEnabled,
                    autoArchive: false,
                    _currentItemId,
                    DateTimeOffset.Now,
                    requestCancellation.Token);
                diagnosticsScope?.Complete(new GenerationQualityObservation(
                    diagnosticsScope.RequestCount == 0 ? "not_generated" : result.Response.Kind switch
                    {
                        PolishResponseKind.Final => "final",
                        PolishResponseKind.NeedsClarification => "needs_clarification",
                        _ => "invalid"
                    },
                    diagnosticsScope.RequestCount > 0 ? result.Response.Kind != PolishResponseKind.Invalid : null,
                    client is IStructuredTextGenerationClient && diagnosticsScope.RequestCount > 0
                        ? result.Response.Kind != PolishResponseKind.Invalid
                        : null,
                    diagnosticsScope.RequestCount > 0
                        ? result.Response.Kind == PolishResponseKind.Final
                            ? result.ValidationIssues.Count == 0
                            : result.Response.Kind == PolishResponseKind.Invalid ? false : null
                        : null,
                    result.WasRepaired,
                    result.ValidationIssues.Count));
                _lastGenerationProfileId = profile.Id;
                _lastGenerationModelName = profile.Model;
                LastUsedProviderLabel = $"本次实际模型：{profile.Name} · {profile.Model}（{(profile.Id == route.Profile.Id ? ProviderRouter.DescribeReason(route.Reason, route.ModelSelectionReason) : "主模型失败后切换到明确配置的备用模型")}）";
                OnPropertyChanged(nameof(LastUsedProviderLabel));
                ThrowIfRequestIsStale(requestVersion, requestCancellation.Token);
                if (shouldArchive && result.Response.Kind == PolishResponseKind.Final)
                {
                    result = new PolishWorkflowResult
                    {
                        Response = result.Response,
                        SavedRevision = SavePolishRevision(polishRequest, result.Response, profile),
                        CompanionEmotion = result.CompanionEmotion,
                        WasRepaired = result.WasRepaired,
                        ValidationIssues = result.ValidationIssues
                    };
                }
                _preferenceSignalTask = ApplicationMode.Polish;
                _preferenceSignalScenario = polishRequest.Scenario;
                _preferenceSignalOutputStyle = polishRequest.OutputStyle;
                if (result.Response.Kind == PolishResponseKind.Final)
                {
                    LastUsedOutputStyleLabel = $"最近成稿风格：{polishRequest.OutputStyle}";
                    OnPropertyChanged(nameof(LastUsedOutputStyleLabel));
                }
                ApplyPolishResult(result);
            }
            else
            {
                var promptCategory = SelectedCategory;
                var promptPreferenceScenario = promptCategory.GetDisplayName();
                var request = new PromptRequest
                {
                    UserInput = parsedInput.Body,
                    Category = promptCategory,
                    Depth = SelectedDepth,
                    OutputStyle = OutputStylePreferenceResolver.Resolve(
                        ApplicationMode.PromptOptimize,
                        promptPreferenceScenario,
                        App.Settings.OutputStyle,
                        App.Settings.OutputStyleOverrides),
                    Persona = App.Settings.UserPersona,
                    CustomSystemPrompt = EffectiveCustomSystemPrompt(),
                    PreferenceInstructions = BuildPreferenceInstructions(
                        ApplicationMode.PromptOptimize,
                        parsedInput.Instructions,
                        promptPreferenceScenario,
                        includeConfirmedPreferences,
                        includeLegacyPreferences)
                };
                var externalStrategy = ResolveExternalStrategy(ApplicationMode.PromptOptimize, parsedInput.Body, string.Empty, promptCategory);
                var plan = _professionalizationPlanner.Create(new ProfessionalizationRequest
                {
                    Input = parsedInput.Body,
                    Mode = ApplicationMode.PromptOptimize,
                    Category = promptCategory,
                    Depth = SelectedDepth,
                    ExplicitRequirements = parsedInput.Instructions,
                    PreferenceInstructions = request.PreferenceInstructions,
                    PreferredStrategyId = externalStrategy.SkillId,
                    PreferredStrategyName = externalStrategy.DisplayName,
                    StrategyInstructions = externalStrategy.Instructions,
                    SelectedSkillIds = externalStrategy.SelectedSkills,
                    SkillWeights = externalStrategy.SkillWeights,
                    SkillConflictDetected = externalStrategy.ConflictDetected
                });
                if (App.Settings.ClarificationEnabled && plan.NeedsClarification)
                {
                    ClarificationQuestions = plan.ClarificationQuestions;
                    HasClarification = true;
                    _clarificationOriginalInput ??= UserInput;
                    ArchiveStatus = "需要补充关键信息";
                    return;
                }
                if (!App.Settings.ClarificationEnabled && plan.NeedsClarification)
                {
                    var missingInformation = MissingInformationSummary.For(plan);
                    ArchiveStatus = "信息不足，未生成";
                    ShowError($"当前信息不足以完成“{FirstNonEmpty(plan.Purpose, "当前任务")}”。缺少：{missingInformation}。本次未向模型发送请求。补充信息后重试，或开启“关键信息缺失时先询问”。");
                    return;
                }
                var client = CreateGenerationClient();
                using var clientDisposal = client as IDisposable;
                request = new PromptRequest
                {
                    UserInput = request.UserInput,
                    Category = request.Category,
                    Depth = request.Depth,
                    OutputStyle = request.OutputStyle,
                    Persona = request.Persona,
                    CustomSystemPrompt = request.CustomSystemPrompt,
                    PreferenceInstructions = request.PreferenceInstructions,
                    Professionalization = plan
                };
                var transformation = await new PromptOptimizationWorkflowService(client, App.PromptBuilder)
                    .ExecuteAsync(request, plan, requestCancellation.Token);
                diagnosticsScope?.Complete(new GenerationQualityObservation(
                    diagnosticsScope.RequestCount == 0 ? "not_generated" : transformation.IsBlocked ? "blocked" : "final",
                    transformation.StructuredOutputValid,
                    transformation.StructuredOutputValid,
                    diagnosticsScope.RequestCount > 0
                        ? !transformation.IsBlocked && transformation.ValidationIssues.Count == 0
                        : null,
                    transformation.WasRepaired,
                    transformation.ValidationIssues.Count));
                LastUsedProviderLabel = $"本次实际模型：{profile.Name} · {profile.Model}（{(profile.Id == route.Profile.Id ? ProviderRouter.DescribeReason(route.Reason, route.ModelSelectionReason) : "主模型失败后切换到明确配置的备用模型")}）";
                OnPropertyChanged(nameof(LastUsedProviderLabel));
                ThrowIfRequestIsStale(requestVersion, requestCancellation.Token);
                if (transformation.IsBlocked)
                {
                    var emptyResponse = transformation.ValidationIssues.Any(issue => issue.Code == "empty-output");
                    ShowError(emptyResponse
                        ? "模型返回为空，请检查模型配置后重试。"
                        : "结果未通过事实保真检查，系统已阻止展示。请重试或补充关键信息。");
                    return;
                }
                _lastGenerationProfileId = profile.Id;
                _lastGenerationModelName = profile.Model;
                var result = transformation.Content;
                if (string.IsNullOrWhiteSpace(result))
                {
                    ShowError("模型返回为空，请重试。");
                    return;
                }
                _preferenceSignalTask = ApplicationMode.PromptOptimize;
                _preferenceSignalScenario = promptPreferenceScenario;
                _preferenceSignalOutputStyle = request.OutputStyle;
                LastUsedOutputStyleLabel = $"最近成稿风格：{request.OutputStyle}";
                OnPropertyChanged(nameof(LastUsedOutputStyleLabel));
                BeginPreferenceFeedback();
                RecordWorkspaceUndo();
                OptimizedResult = result;
                ViewMode = ViewMode.Optimized;
                // 生成结果进入与历史成稿一致的可编辑状态，用户无需再寻找隐藏的“编辑”动作。
                _generatedResultBeforeEdit = result;
                IsEditingResult = true;
                AddHistory(UserInput.Trim());
                if (ShouldArchive())
                {
                    var revision = _archiveService.SaveRevision(new ArchiveDraft
                    {
                        Mode = ApplicationMode.PromptOptimize,
                        OriginalText = App.Settings.SaveOriginalText ? UserInput : string.Empty,
                        FinalText = App.Settings.SaveOptimizedText ? result : string.Empty,
                        Scenario = "提示词优化",
                        Topic = SelectedCategory.GetDisplayName(),
                        ContextJson = JsonSerializer.Serialize(new { Category = SelectedCategory, Depth = SelectedDepth }),
                        Style = request.OutputStyle,
                        ModelProfileId = profile.Id,
                        ModelName = profile.Model
                    }, DateTimeOffset.Now);
                    _currentItemId = revision.ItemId;
                    _lastSavedRevision = revision;
                    ArchiveStatus = $"已采用：{plan.StrategyName} · 已归档 v{revision.Version:00}";
                    RefreshHistory();
                    StartUndoWindow();
                }
                else
                {
                    IsResultUnarchived = true;
                    ArchiveStatus = App.Settings.IncognitoMode ? $"已采用：{plan.StrategyName} · 无痕模式" : $"已采用：{plan.StrategyName}";
                }
                TryAutoCopyResult();
                ApplyAssistantEmotion(transformation.CompanionEmotion);
                MarkDraftDirty();
            }
        }
        catch (OperationCanceledException)
        {
            diagnosticsScope?.Complete(new GenerationQualityObservation("cancelled", null, null, null, false, 0));
            ArchiveStatus = "已取消";
        }
        catch (GenerationFailureException failure)
        {
            diagnosticsScope?.Complete(new GenerationQualityObservation("failed", null, null, null, false, 0));
            LastGenerationFailure = failure;
            ShowError($"生成失败：{failure.Message}");
        }
        catch (Exception ex)
        {
            diagnosticsScope?.Complete(new GenerationQualityObservation("failed", null, null, null, false, 0));
            ShowError($"生成失败：{ex.Message}");
        }
        finally
        {
            if (requestVersion == Volatile.Read(ref _requestVersion))
            {
                var completed = !HasError && !HasClarification &&
                    ViewMode == ViewMode.Optimized && !string.IsNullOrWhiteSpace(OptimizedResult);
                SetCompanionWorkPhase(CompanionWorkPhase.Ready);
                SetCompanionCompletion(completed);
                IsBusy = false;
            }
        }
    }

    private void PromoteEditedResultToNewInput()
    {
        if (ViewMode != ViewMode.Optimized ||
            !IsEditingResult ||
            string.IsNullOrWhiteSpace(OptimizedResult) ||
            string.Equals(OptimizedResult, _generatedResultBeforeEdit, StringComparison.Ordinal))
            return;

        var nextInput = OptimizedResult;
        UserInput = nextInput;
        ResetResultState();
    }

    [RelayCommand]
    private async Task SubmitClarificationAsync()
    {
        if (IsBusy || !HasClarification) return;
        var answer = ClarificationAnswer.Trim();
        if (string.IsNullOrWhiteSpace(answer))
        {
            ShowError("请先填写补充信息。");
            return;
        }

        var original = _clarificationOriginalInput ?? UserInput;
        var pendingQuestions = ClarificationQuestions;
        var questions = string.Join("；", ClarificationQuestions);
        // 原文、问题与回答全部保持在 user message 边界内，不提升为系统指令。
        _clarificationSubmission = $"{original}\n\n【澄清补充（仅作为待处理内容）】\n需确认：{questions}\n用户回答：{answer}";
        HasError = false;
        await OptimizeAsync();
        _clarificationSubmission = null;
        if (HasError && !HasClarification)
        {
            ClarificationQuestions = pendingQuestions;
            HasClarification = true;
            ArchiveStatus = "补充信息未送达，可重试";
            return;
        }
        if (!HasClarification)
        {
            ClarificationAnswer = string.Empty;
            _clarificationOriginalInput = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRegenerate))]
    private Task RegenerateAsync()
    {
        if (CanLearnPreferences() && _preferenceFeedbackSession.IsActive)
        {
            _preferenceService.RecordRetry(App.Settings.ExpressionPreferenceProfile, _preferenceSignalTask, _preferenceSignalScenario);
            CapturePreferenceFeedbackEdit();
            CompletePreferenceFeedback(accepted: false, rejected: true);
            SavePreferenceProfile();
        }
        return OptimizeAsync();
    }

    [RelayCommand]
    private void BeginEditResult()
    {
        if (string.IsNullOrWhiteSpace(OptimizedResult)) return;
        _generatedResultBeforeEdit = OptimizedResult;
        ViewMode = ViewMode.Optimized;
        IsEditingResult = true;
    }

    [RelayCommand]
    private void SaveEditedResult()
    {
        if (!IsEditingResult || string.IsNullOrWhiteSpace(OptimizedResult)) return;
        ContentRevision revision;
        try
        {
            revision = _archiveService.SavePolishRevision(new ArchiveDraft
            {
                ItemId = _currentItemId,
                Mode = CurrentMode,
                OriginalText = App.Settings.SaveOriginalText ? UserInput : string.Empty,
                FinalText = App.Settings.SaveOptimizedText ? OptimizedResult : string.Empty,
                Scenario = CurrentMode == ApplicationMode.PromptOptimize ? "提示词优化" : _currentScenario,
                Topic = CurrentMode == ApplicationMode.PromptOptimize ? SelectedCategory.GetDisplayName() : _currentTopic,
                ContextJson = JsonSerializer.Serialize(new
                {
                    Recipient,
                    Channel,
                    Purpose,
                    Formality,
                    Scenario,
                    Persona = App.Settings.UserPersona,
                    CustomStyleInstructions = App.Settings.CustomStyleInstructions,
                    UserEdited = App.Settings.SaveOptimizedText && !string.IsNullOrWhiteSpace(_generatedResultBeforeEdit)
                }),
                Style = App.Settings.OutputStyle,
                ModelProfileId = string.IsNullOrWhiteSpace(_lastGenerationProfileId)
                    ? App.Settings.GetActiveProviderProfile().Id
                    : _lastGenerationProfileId,
                ModelName = string.IsNullOrWhiteSpace(_lastGenerationModelName)
                    ? App.Settings.GetActiveProviderProfile().Model
                    : _lastGenerationModelName
            }, DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            ShowError($"保存归档失败：{ex.Message}");
            return;
        }
        _currentItemId = revision.ItemId;
        _lastSavedRevision = revision;
        IsEditingResult = false;
        if (CanLearnPreferences() && _preferenceFeedbackSession.IsActive && !string.IsNullOrWhiteSpace(_generatedResultBeforeEdit))
        {
            _preferenceService.RecordEdit(
                App.Settings.ExpressionPreferenceProfile,
                _generatedResultBeforeEdit,
                OptimizedResult,
                _preferenceSignalTask,
                _preferenceSignalScenario);
            RecordPreferenceFeedbackEdit(_generatedResultBeforeEdit, OptimizedResult);
            SavePreferenceProfile();
        }
        _generatedResultBeforeEdit = string.Empty;
        ArchiveStatus = $"已归档 v{revision.Version:00}";
        RefreshHistory();
        StartUndoWindow();
    }

    [RelayCommand]
    private void UndoArchive()
    {
        if (_lastSavedRevision is null || !CanUndoArchive) return;
        try
        {
            _archiveService.SoftDeleteRevision(_lastSavedRevision.Id, DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            ShowError($"撤销归档失败：{ex.Message}");
            return;
        }
        CanUndoArchive = false;
        ArchiveStatus = "已撤销归档";
    }

    private PolishRequest CreatePolishRequest(
        ParsedInputContext parsed,
        ProviderProfile providerProfile,
        bool includeConfirmedPreferences,
        bool includeLegacyPreferences)
    {
        var intelligence = _contextAnalyzer.Analyze(parsed.Body, _sourceApplicationContext);
        _sourceApplicationContext = null;
        var requestedScenario = FirstNonEmpty(parsed.Scenario, Scenario);
        if (string.Equals(requestedScenario, "其他", StringComparison.Ordinal)) requestedScenario = string.Empty;
        var explicitContext = new ParsedInputContext(
            parsed.Body,
            FirstNonEmpty(parsed.Recipient, Recipient),
            FirstNonEmpty(parsed.Channel, Channel),
            FirstNonEmpty(parsed.Purpose, Purpose),
            FirstNonEmpty(parsed.Formality, Formality),
            requestedScenario,
            parsed.Weight,
            parsed.Instructions);
        var resolved = SmartContextAnalyzer.Merge(explicitContext, intelligence, App.Settings.DefaultPolishScenario);
        var preferenceInstructions = BuildPreferenceInstructions(
            ApplicationMode.Polish,
            parsed.Instructions,
            resolved.Scenario,
            includeConfirmedPreferences,
            includeLegacyPreferences);
        var externalStrategy = ResolveExternalStrategy(ApplicationMode.Polish, parsed.Body, resolved.Scenario, SelectedCategory);
        var plan = _professionalizationPlanner.Create(new ProfessionalizationRequest
        {
            Input = parsed.Body,
            Mode = ApplicationMode.Polish,
            Recipient = resolved.Recipient,
            Scenario = resolved.Scenario,
            Purpose = resolved.Purpose,
            PurposeIsExplicit = !string.IsNullOrWhiteSpace(parsed.Purpose) || !string.IsNullOrWhiteSpace(Purpose),
            Formality = resolved.Formality,
            ExplicitRequirements = parsed.Instructions,
            PreferenceInstructions = preferenceInstructions,
            PreferredStrategyId = externalStrategy.SkillId,
            PreferredStrategyName = externalStrategy.DisplayName,
            StrategyInstructions = externalStrategy.Instructions,
            SelectedSkillIds = externalStrategy.SelectedSkills,
            SkillWeights = externalStrategy.SkillWeights,
            SkillConflictDetected = externalStrategy.ConflictDetected
        });
        return new PolishRequest
        {
            OriginalText = parsed.Body,
            Recipient = resolved.Recipient,
            Channel = resolved.Channel,
            Purpose = resolved.Purpose,
            Formality = resolved.Formality,
            Scenario = resolved.Scenario,
            OutputStyle = OutputStylePreferenceResolver.Resolve(
                ApplicationMode.Polish,
                resolved.Scenario,
                App.Settings.OutputStyle,
                App.Settings.OutputStyleOverrides),
            CustomStyleInstructions = App.Settings.CustomStyleInstructions,
            Persona = App.Settings.UserPersona,
            CustomSystemPrompt = EffectiveCustomSystemPrompt(),
            PreferenceInstructions = preferenceInstructions,
            Professionalization = plan,
            Intelligence = intelligence,
            ModelProfileId = providerProfile.Id,
            ModelName = providerProfile.Model,
            SaveOriginalText = App.Settings.SaveOriginalText,
            SaveOptimizedText = App.Settings.SaveOptimizedText
        };
    }

    private ProfessionalizationPlan CreateRoutingPlan(ApplicationMode mode, ParsedInputContext parsed)
    {
        return _professionalizationPlanner.Create(new ProfessionalizationRequest
        {
            Input = parsed.Body,
            Mode = mode,
            Category = SelectedCategory,
            Depth = SelectedDepth,
            ExplicitRequirements = parsed.Instructions
        });
    }

    private void ThrowIfRequestIsStale(long requestVersion, CancellationToken cancellationToken)
    {
        if (requestVersion != Volatile.Read(ref _requestVersion)) throw new OperationCanceledException(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private ContentRevision SavePolishRevision(PolishRequest request, PolishResponse response, ProviderProfile profile) =>
        _archiveService.SavePolishRevision(new ArchiveDraft
        {
            ItemId = _currentItemId,
            OriginalText = request.SaveOriginalText ? request.OriginalText : string.Empty,
            FinalText = request.SaveOptimizedText ? response.Content : string.Empty,
            Scenario = response.Scenario,
            Topic = response.Topic,
            ContextJson = JsonSerializer.Serialize(new
            {
                request.Recipient,
                request.Channel,
                request.Purpose,
                request.Formality,
                request.Scenario,
                request.Persona,
                request.CustomStyleInstructions
            }),
            Style = request.OutputStyle,
            ModelProfileId = profile.Id,
            ModelName = profile.Model
        }, DateTimeOffset.Now);

    private void ApplyPolishResult(PolishWorkflowResult result)
    {
        switch (result.Response.Kind)
        {
            case PolishResponseKind.Final:
                if (string.IsNullOrWhiteSpace(result.Response.Content))
                {
                    OptimizedResult = string.Empty;
                    IsResultUnarchived = true;
                    ViewMode = ViewMode.Original;
                    ShowError("模型返回为空，请重试或检查模型配置。");
                    break;
                }
                RecordWorkspaceUndo();
                BeginPreferenceFeedback();
                OptimizedResult = result.Response.Content;
                _generatedResultBeforeEdit = result.Response.Content;
                _currentScenario = string.IsNullOrWhiteSpace(result.Response.Scenario) ? "其他" : result.Response.Scenario;
                _currentTopic = string.IsNullOrWhiteSpace(result.Response.Topic) ? "未命名表达" : result.Response.Topic;
                ViewMode = ViewMode.Optimized;
                // 润色成稿与提示词优化结果保持一致：生成后即可直接修改，无需额外寻找编辑入口。
                IsEditingResult = true;
                AddHistory(UserInput.Trim());
                if (result.SavedRevision is { } revision)
                {
                    _lastSavedRevision = revision;
                    _currentItemId = revision.ItemId;
                    ArchiveStatus = $"已归档 v{revision.Version:00}";
                    StartUndoWindow();
                    RefreshHistory();
                }
                else
                {
                    IsResultUnarchived = true;
                    ArchiveStatus = App.Settings.IncognitoMode ? "无痕模式" : "未归档";
                }
                TryAutoCopyResult();
                ApplyAssistantEmotion(result.CompanionEmotion);
                break;
            case PolishResponseKind.NeedsClarification:
                ClarificationQuestions = result.Response.Questions;
                HasClarification = true;
                _clarificationOriginalInput ??= UserInput;
                ArchiveStatus = "需要补充信息";
                break;
            default:
                OptimizedResult = result.ValidationIssues.Count == 0 ? result.Response.RawText : string.Empty;
                IsResultUnarchived = true;
                ViewMode = string.IsNullOrEmpty(OptimizedResult) ? ViewMode.Original : ViewMode.Optimized;
                ShowError(result.ValidationIssues.Count > 0
                    ? "成稿未通过事实保真检查，系统已阻止展示和归档。请重试或补充关键信息。"
                    : "模型返回格式异常，当前内容未归档。请重试或检查模型能力。");
                break;
        }
    }

    private void StartUndoWindow()
    {
        _undoCancellation?.Cancel();
        _undoCancellation?.Dispose();
        _undoCancellation = new CancellationTokenSource();
        CanUndoArchive = true;
        _ = ExpireUndoAsync(_undoCancellation.Token);
    }

    private async Task ExpireUndoAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            CanUndoArchive = false;
        }
        catch (OperationCanceledException) { }
    }

    private void ResetResultState()
    {
        OptimizedResult = string.Empty;
        ViewMode = ViewMode.Original;
        ClarificationQuestions = [];
        HasClarification = false;
        ClarificationAnswer = string.Empty;
        _clarificationSubmission = null;
        _clarificationOriginalInput = null;
        ArchiveStatus = string.Empty;
        CanUndoArchive = false;
        IsResultUnarchived = false;
        IsEditingResult = false;
        _showDiff = false;
        _lastSavedRevision = null;
        _currentItemId = null;
        _generatedResultBeforeEdit = string.Empty;
        _preferenceFeedbackSession.Cancel();
    }

    private void BeginPreferenceFeedback()
    {
        _preferenceFeedbackSession.Start(
            _preferenceSignalTask,
            _preferenceSignalScenario,
            _preferenceSignalOutputStyle,
            CanLearnPreferences());
    }

    private void RecordPreferenceFeedbackEdit(string? generated, string? edited)
    {
        _preferenceFeedbackSession.RecordEdit(generated, edited);
    }

    private void CapturePreferenceFeedbackEdit()
    {
        if (string.IsNullOrWhiteSpace(_generatedResultBeforeEdit) ||
            string.Equals(_generatedResultBeforeEdit, OptimizedResult, StringComparison.Ordinal))
            return;

        RecordPreferenceFeedbackEdit(_generatedResultBeforeEdit, OptimizedResult);
    }

    private void CompletePreferenceFeedback(bool accepted, bool rejected)
    {
        _preferenceFeedbackSession.Complete(
            App.Settings.ExpressionPreferenceProfile, _preferenceService, accepted, rejected);
    }

    private void ShowError(string message)
    {
        ApplyAssistantEmotion(null);
        if (System.Windows.Application.Current is App app)
            app.LogError("Generation", new InvalidOperationException(message));
        ErrorMessage = message;
        HasError = true;
    }

    private void ApplyAssistantEmotion(AssistantEmotionHint? hint)
    {
        _assistantEmotionCancellation?.Cancel();
        _assistantEmotionCancellation?.Dispose();
        _assistantEmotionCancellation = null;
        _assistantEmotion = App.Settings.CompanionDriverMode == CompanionDriverMode.EmotionAssistant ? hint : null;
        NotifyCompanionFeedbackChanged();
        if (_assistantEmotion is null) return;

        var cancellation = new CancellationTokenSource();
        _assistantEmotionCancellation = cancellation;
        _ = ClearAssistantEmotionAfterDelayAsync(cancellation);
    }

    private async Task ClearAssistantEmotionAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            var intensity = _assistantEmotion?.Intensity ?? 0.5;
            await Task.Delay(TimeSpan.FromMilliseconds(1800 + intensity * 1800), cancellation.Token);
            if (ReferenceEquals(_assistantEmotionCancellation, cancellation))
            {
                _assistantEmotion = null;
                _assistantEmotionCancellation = null;
                NotifyCompanionFeedbackChanged();
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!ReferenceEquals(_assistantEmotionCancellation, cancellation)) cancellation.Dispose();
        }
    }

    private void AddHistory(string text)
    {
        if (!App.Settings.HistoryEnabled || App.Settings.IncognitoMode) return;
        if (string.IsNullOrEmpty(text)) return;
        var list = App.Settings.History;
        list.RemoveAll(x => x == text);
        list.Insert(0, text);
        while (list.Count > App.Settings.PromptHistoryLimit) list.RemoveAt(list.Count - 1);
        OnPropertyChanged(nameof(History));
    }

    private WorkspaceDraft CaptureWorkspace() => new()
    {
        UserInput = UserInput,
        OptimizedResult = OptimizedResult,
        CurrentMode = CurrentMode,
        ViewMode = ViewMode,
        SelectedCategory = SelectedCategory,
        SelectedDepth = SelectedDepth,
        ActiveProviderProfileId = App.Settings.ActiveProviderProfileId,
        Recipient = Recipient,
        Channel = Channel,
        Purpose = Purpose,
        Formality = Formality,
        Scenario = Scenario,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private void RestoreWorkspace(WorkspaceDraft draft)
    {
        _restoringWorkspace = true;
        try
        {
            UserInput = draft.UserInput ?? string.Empty;
            OptimizedResult = draft.OptimizedResult ?? string.Empty;
            CurrentMode = App.Settings.EnabledModes.Contains(draft.CurrentMode)
                ? draft.CurrentMode
                : App.Settings.DefaultMode;
            ViewMode = draft.ViewMode == ViewMode.Optimized && !string.IsNullOrEmpty(OptimizedResult)
                ? ViewMode.Optimized
                : ViewMode.Original;
            SelectedCategory = App.Settings.EnabledPromptCategories.Contains(draft.SelectedCategory)
                ? draft.SelectedCategory
                : App.Settings.GetDefaultCategory();
            SelectedDepth = PromptDepthMetadata.AllDepths.Contains(draft.SelectedDepth)
                ? draft.SelectedDepth
                : App.Settings.GetDefaultDepth();
            Recipient = draft.Recipient ?? string.Empty;
            Channel = draft.Channel ?? string.Empty;
            Purpose = draft.Purpose ?? string.Empty;
            Formality = draft.Formality ?? string.Empty;
            Scenario = draft.Scenario ?? string.Empty;
            if (App.Settings.ProviderProfiles.Any(profile => profile.Id == draft.ActiveProviderProfileId))
            {
                App.Settings.ActiveProviderProfileId = draft.ActiveProviderProfileId;
            }
            OnPropertyChanged(nameof(ActiveProviderLabel));
            OnPropertyChanged(nameof(ActiveProviderProfile));
        }
        finally
        {
            _restoringWorkspace = false;
            _draftDirty = false;
        }
    }

    private void RecordWorkspaceUndo()
    {
        if (_restoringWorkspace) return;
        var snapshot = CaptureWorkspace();
        var undo = UndoStack;
        if (undo.TryPeek(out var previous) && SameWorkspace(previous, snapshot)) return;
        undo.Push(snapshot);
        RedoStack.Clear();
        NotifyWorkspaceHistoryChanged();
    }

    /// <summary>当前模式的撤回栈（惰性创建）。</summary>
    private Stack<WorkspaceDraft> UndoStack => GetOrCreateStack(_undoByMode, CurrentMode);

    /// <summary>当前模式的重做栈（惰性创建）。</summary>
    private Stack<WorkspaceDraft> RedoStack => GetOrCreateStack(_redoByMode, CurrentMode);

    private static Stack<WorkspaceDraft> GetOrCreateStack(
        Dictionary<ApplicationMode, Stack<WorkspaceDraft>> stacks, ApplicationMode mode)
    {
        if (!stacks.TryGetValue(mode, out var stack))
        {
            stack = new Stack<WorkspaceDraft>();
            stacks[mode] = stack;
        }
        return stack;
    }

    private static bool SameWorkspace(WorkspaceDraft left, WorkspaceDraft right) =>
        left.UserInput == right.UserInput &&
        left.OptimizedResult == right.OptimizedResult &&
        left.CurrentMode == right.CurrentMode &&
        left.ViewMode == right.ViewMode &&
        left.SelectedCategory == right.SelectedCategory &&
        left.SelectedDepth == right.SelectedDepth &&
        left.ActiveProviderProfileId == right.ActiveProviderProfileId &&
        left.Recipient == right.Recipient &&
        left.Channel == right.Channel &&
        left.Purpose == right.Purpose &&
        left.Formality == right.Formality &&
        left.Scenario == right.Scenario;

    private void NotifyWorkspaceHistoryChanged()
    {
        OnPropertyChanged(nameof(CanUndoWorkspace));
        OnPropertyChanged(nameof(CanRedoWorkspace));
        UndoWorkspaceCommand.NotifyCanExecuteChanged();
        RedoWorkspaceCommand.NotifyCanExecuteChanged();
    }

    private void MarkDraftDirty()
    {
        if (!_restoringWorkspace) _draftDirty = true;
    }

    private string EffectiveCustomSystemPrompt() =>
        string.IsNullOrWhiteSpace(SelectedPreset?.CustomSystemPrompt)
            ? App.Settings.CustomSystemPrompt
            : SelectedPreset.CustomSystemPrompt;

    private string BuildPreferenceInstructions(
        ApplicationMode task,
        string? inlineInstructions = null,
        string? scenario = null,
        bool includeConfirmedPreferences = false,
        bool includeLegacyPreferences = false)
    {
        var preferences = new List<string>();
        if (App.Settings.PreserveMeaning) preferences.Add("必须保留原意、事实与立场");
        if (App.Settings.MinimalRewrite) preferences.Add("减少非必要改写，只调整影响理解和表达的问题");
        if (App.Settings.ProfessionalTone) preferences.Add("表达专业、准确、克制");
        if (!string.IsNullOrWhiteSpace(SelectedPreset?.Instructions)) preferences.Add(SelectedPreset.Instructions.Trim());
        if (!string.IsNullOrWhiteSpace(inlineInstructions)) preferences.Add(inlineInstructions.Trim());
        var confirmed = includeConfirmedPreferences
            ? _preferenceService.BuildInstructions(App.Settings.ExpressionPreferenceProfile, task, scenario, includeLegacyPreferences)
            : string.Empty;
        if (!string.IsNullOrWhiteSpace(confirmed)) preferences.Add(confirmed);
        return string.Join("；", preferences);
    }

    private static ExpressionSkillRouteResult ResolveExternalStrategy(ApplicationMode mode, string input, string scenario, PromptCategory category)
    {
        return new ExpressionSkillRouter(System.IO.Path.Combine(App.DataRoot, "agent-skills")).Route(new ExpressionSkillRoutingContext
        {
            Mode = mode,
            Input = input,
            Scenario = scenario,
            Category = category
        });
    }

    private static string FirstNonEmpty(string first, string fallback)
        => string.IsNullOrWhiteSpace(first) ? fallback : first;

    private bool ShouldArchive() =>
        App.Settings.HistoryEnabled &&
        App.Settings.AutoArchive &&
        !App.Settings.IncognitoMode &&
        (App.Settings.SaveOriginalText || App.Settings.SaveOptimizedText);

    private void TryAutoCopyResult()
    {
        if (!App.Settings.AutoCopyAfterOptimize || string.IsNullOrWhiteSpace(OptimizedResult)) return;
        try { _clipboardService.CopyText(OptimizedResult); }
        catch (Exception ex) { DraftStatus = $"自动复制失败：{ex.Message}"; }
    }

    private static bool CanLearnPreferences() => App.Settings.PreferenceLearningEnabled && !App.Settings.IncognitoMode;

    private static void SavePreferenceProfile()
    {
        // Unit construction must never write the signed-in user's real config; the desktop app persists normally.
        if (System.Windows.Application.Current is not App) return;
        try { App.ConfigService.Save(App.Settings); } catch { }
    }
}
