using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using PromptFloat.Models;
using PromptFloat.Services;
using PromptFloat.Views;
using Microsoft.Win32;

namespace PromptFloat;

/// <summary>
/// 应用程序入口。
/// 负责：加载配置、初始化全局服务、注册全局快捷键、管理 MainWindow 与悬浮球的显隐。
/// </summary>
public partial class App : System.Windows.Application
{
    private bool _fatalDialogShown;
    private HotkeyRegistrationResult? _hotkeyRegistration;
    private readonly Lazy<ErrorLogService> _errorLogService = new(() => new ErrorLogService(DataRoot));
    internal static HealthCheckReport? LatestHealthReport { get; private set; }

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>全局配置（加载一次，多处共享）。</summary>
    internal static AppSettings Settings { get; private set; } = new();

    /// <summary>设置保存后通知已打开的悬浮窗刷新绑定数据。</summary>
    internal static event EventHandler? SettingsChanged;

    internal static void ReplaceSettings(AppSettings settings)
    {
        Settings = settings;
        SettingsChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>配置读写服务。</summary>
    internal static ConfigService ConfigService { get; } = new();

    /// <summary>全局快捷键服务。</summary>
    internal static HotkeyService HotkeyService { get; } = new();

    /// <summary>剪贴板服务。</summary>
    internal static ClipboardService ClipboardService { get; } = new();

    /// <summary>皮肤服务（管理整套样式替换）。在启动时初始化。</summary>
    internal static SkinService SkinService { get; private set; } = new();

    /// <summary>提示词组装服务。</summary>
    internal static PromptBuilderService PromptBuilder { get; } = new();

    internal static PolishPromptBuilderService PolishPromptBuilder { get; } = new();

    internal static string DefaultDataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Huaxiazi");
    internal static string DataRoot => DataDirectoryPolicy.ResolveOrDefault(Settings.DataDirectory, DefaultDataRoot);

    private static readonly Lazy<ArchiveService> ArchiveServiceInstance = new(() => new ArchiveService(DataRoot));
    internal static ArchiveService ArchiveService => ArchiveServiceInstance.Value;

    private static readonly Lazy<DpapiSecretStore> SecretStoreInstance = new(() =>
        new DpapiSecretStore(Path.Combine(DataRoot, "secrets")));
    internal static DpapiSecretStore SecretStore => SecretStoreInstance.Value;

    private static readonly Lazy<WorkspaceDraftService> WorkspaceDraftServiceInstance = new(() =>
        new WorkspaceDraftService(DataRoot));
    internal static WorkspaceDraftService WorkspaceDraftService => WorkspaceDraftServiceInstance.Value;

    /// <summary>主窗口（展开态）。延迟创建。</summary>
    private MainWindow? _mainWindow;

    /// <summary>悬浮球窗口（收缩态）。</summary>
    private FloatingBallWindow? _floatingBall;
    private Point? _ballOriginBeforeExpand;
    private readonly FloatingUiStateMachine _floatingUi = new();
    private SingleInstanceGuard? _singleInstance;
    private string _singleInstanceWarning = string.Empty;
    private UserPreferenceChangedEventHandler? _systemThemeChangedHandler;
    private TrayIconService? _trayIcon;
    internal bool IsExiting { get; private set; }

    /// <summary>应用启动：加载配置、注册热键、显示悬浮球。</summary>
    private void App_OnStartup(object sender, StartupEventArgs e)
    {
        var instanceResult = SingleInstanceGuard.Acquire("Vesper.Huaxiazi");
        if (instanceResult.Status == SingleInstanceAcquireStatus.AlreadyRunning)
        {
            Shutdown();
            return;
        }
        _singleInstance = instanceResult.Guard;
        if (_singleInstance is not null)
        {
            _singleInstance.ActivationRequested += (_, _) =>
                Dispatcher.BeginInvoke(new Action(ShowMainWindow));
        }
        else if (instanceResult.Status == SingleInstanceAcquireStatus.Unavailable)
        {
            // Availability wins over the singleton optimization in restricted sessions.
            _singleInstanceWarning = instanceResult.Message;
        }
        // 1. 加载配置（不存在则写默认）
        Settings = ConfigService.Load();
        Settings.NormalizeResidentEntrypoints();
        try
        {
            new AgentSkillPackageService(Path.Combine(DataRoot, "agent-skills"))
                .ImportPresets(Path.Combine(AppContext.BaseDirectory, "Presets", "Skills"));
        }
        catch (Exception exception) { LogUnhandled("PresetSkillImport", exception); }
        SkinService = new(Application.Current.Resources);
        try
        {
            foreach (var skin in new SkinPackageService(Path.Combine(DataRoot, "skins")).DiscoverInstalled())
                SkinService.Register(skin);
        }
        catch (Exception exception) { LogUnhandled("SkinCatalog", exception); }
        ThemeService.Apply(Settings, SkinService, Application.Current.Resources);
        _systemThemeChangedHandler = (_, args) =>
        {
            if (args.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color)) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (string.Equals(Settings.ThemeMode, "System", StringComparison.OrdinalIgnoreCase) || SystemParameters.HighContrast)
                {
                    ThemeService.Apply(Settings, SkinService, Application.Current.Resources);
                }
            }));
        };
        SystemEvents.UserPreferenceChanged += _systemThemeChangedHandler;
        try { _errorLogService.Value.Prepare(); }
        catch { }
        if (!string.IsNullOrWhiteSpace(_singleInstanceWarning))
            LogUnhandled("SingleInstance", new InvalidOperationException(_singleInstanceWarning));
        StartupService.TrySetEnabled(Settings.StartWithWindows, out _);
        try { ArchiveService.PurgeDeletedBefore(DateTimeOffset.UtcNow.AddDays(-Settings.HistoryRetentionDays)); }
        catch (Exception exception) { LogUnhandled("DatabaseRetention", exception); }

        // 2. 注册用户已配置的全局快捷键；默认只占用一组呼出键。
        HotkeyService.ActionPressed += OnHotkeyActionPressed;
        try
        {
            _hotkeyRegistration = HotkeyService.SetHotkeys(BuildHotkeyBindings(Settings));
        }
        catch (Exception ex)
        {
            _hotkeyRegistration = FailedHotkeyRegistration(ex.Message);
            // 快捷键不是启动前置条件。健康检查会记录，设置页可重新配置，不弹模态框打断启动。
            LogUnhandled("HotkeyRegistration", ex);
        }

        // 3. 托盘是稳定入口；悬浮球可在设置中关闭。
        if (Settings.TrayEnabled)
        {
            _trayIcon = new TrayIconService(
                () => Current.Dispatcher.Invoke(ShowMainWindow),
                () => Current.Dispatcher.Invoke(OpenSettings),
                () => Current.Dispatcher.Invoke(ExitApp));
            _trayIcon.Show();
        }
        if (e.Args.Any(arg => string.Equals(arg, "--show-main", StringComparison.OrdinalIgnoreCase)))
        {
            ShowFloatingBall();
            Dispatcher.BeginInvoke(ShowMainWindow);
        }
        else if (Settings.FloatingBallEnabled) ShowFloatingBall();

        Dispatcher.BeginInvoke(new Action(async () => await RunHealthChecksAsync(showLocalFailureDialog: true)));

        if (Settings.AutoCheckUpdates && !string.IsNullOrWhiteSpace(Settings.UpdateCheckUrl))
        {
            Dispatcher.BeginInvoke(new Action(CheckUpdatesOnStartupAsync));
        }
    }

    internal async Task<HealthCheckReport> RunHealthChecksAsync(bool showLocalFailureDialog = false)
    {
        var report = await new HealthCheckService().CheckAsync(
            Settings,
            DataRoot,
            secretId => SecretStore.Read(secretId),
            _hotkeyRegistration ?? FailedHotkeyRegistration("快捷键尚未初始化"));
        LatestHealthReport = report;
        try
        {
            Directory.CreateDirectory(DataRoot);
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(DataRoot, "health-check.json"), json);
        }
        catch { }

        if (showLocalFailureDialog && report.HasLocalFailures)
        {
            var failures = string.Join(Environment.NewLine, report.Items
                .Where(item => item.Scope == HealthCheckScope.Local && item.Status == HealthCheckStatus.Failed)
                .Select(item => $"• {item.Name}: {item.Message}"));
            MessageBox.Show("本地健康检查未通过：\n" + failures + "\n\n详细结果已写入数据目录的 health-check.json。",
                "Vesper 健康检查", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return report;
    }

    internal void RecordHotkeyRegistration(HotkeyRegistrationResult result) => _hotkeyRegistration = result;

    private static HotkeyRegistrationResult FailedHotkeyRegistration(string message) => new()
    {
        Actions = Enum.GetValues<GlobalHotkeyAction>().ToDictionary(
            action => action,
            _ => new HotkeyActionRegistration(false, message))
    };

    private async void CheckUpdatesOnStartupAsync()
    {
        try
        {
            var result = await new UpdateChecker().CheckAsync(Settings.UpdateCheckUrl);
            if (result.Status != UpdateStatus.UpdateAvailable) return;
            var notes = string.IsNullOrWhiteSpace(result.ReleaseNotes) ? string.Empty : $"\n\n{result.ReleaseNotes}";
            MessageBox.Show($"发现 Vesper {result.LatestVersion}。可在设置的‘关于与更新’中下载。{notes}",
                "Vesper 更新", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch
        {
            // 自动检查必须静默降级；手动检查仍会给出明确状态。
        }
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        LogUnhandled("UI", e.Exception);
        // UI 异常不能让模态对话框留下“所有按钮都失效”的半禁用状态。
        // 在当前消息回调结束后恢复仍可见但被禁用的窗口，详细异常保留在 errors.log。
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                foreach (Window window in Windows)
                    if (window.IsVisible && !window.IsEnabled) window.IsEnabled = true;
            }));
        }
        catch { }
        e.Handled = true;
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is not Exception exception) return;
        LogUnhandled("Process", exception);
        if (!ExceptionPresentationPolicy.ShouldShowFatalDialog(UnhandledExceptionOrigin.AppDomain)) return;
        try { Current?.Dispatcher.BeginInvoke(ShowFatalError); }
        catch { ShowFatalError(); }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogUnhandled("Task", e.Exception);
        e.SetObserved();
    }

    private void LogUnhandled(string source, Exception exception)
    {
        try { _errorLogService.Value.Write(source, exception); }
        catch { }
    }

    private void ShowFatalError()
    {
        if (_fatalDialogShown) return;
        _fatalDialogShown = true;
        try
        {
            MessageBox.Show("Vesper 遇到不可恢复错误，即将退出。详细信息已写入数据目录中的 errors.log。", "Vesper", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }

    /// <summary>
    /// 热键命中：确保主窗口可见并置顶。需在 UI 线程执行。
    /// </summary>
    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        Current.Dispatcher.Invoke(ShowMainWindow);
    }

    private void OnHotkeyActionPressed(object? sender, GlobalHotkeyEventArgs e)
    {
        // 必须在显示 Vesper 之前读取，否则前台进程会变成 Vesper 自身，丢失来源上下文。
        var sourceContext = e.Action == GlobalHotkeyAction.QuickPolish
            ? ForegroundApplicationContextService.Capture()
            : null;
        Current.Dispatcher.Invoke(() =>
        {
            ShowMainWindow();
            if (sourceContext is not null) _mainWindow?.SetSourceApplicationContext(sourceContext);
            _mainWindow?.ExecuteGlobalHotkeyAction(e.Action);
        });
    }

    internal static System.Collections.Generic.IReadOnlyDictionary<GlobalHotkeyAction, string> BuildHotkeyBindings(AppSettings settings) =>
        new System.Collections.Generic.Dictionary<GlobalHotkeyAction, string>
        {
            [GlobalHotkeyAction.ToggleWindow] = settings.Hotkey,
            [GlobalHotkeyAction.QuickPolish] = settings.QuickPolishHotkey,
            [GlobalHotkeyAction.QuickPromptOptimize] = settings.QuickPromptHotkey,
            [GlobalHotkeyAction.CopyResult] = settings.CopyResultHotkey
        };

    /// <summary>显示/恢复主窗口（展开态），隐藏悬浮球。</summary>
    internal void ShowMainWindow()
    {
        Point? ballCenter = null;
        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow();
            _mainWindow.Closed += (_, _) => _mainWindow = null;
        }

        var uiScale = Settings.UiScale;
        var desiredWidth = (Settings.RememberWindowSize ? Settings.MainWindowWidth : 520) * uiScale;
        var desiredHeight = (Settings.RememberWindowSize ? Settings.MainWindowHeight : 176) * uiScale;

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.ShowInTaskbar = Settings.ShowInTaskbar;
        }

        // 显示主窗口、置顶并聚焦；同时存在悬浮球时顺手隐藏。
        if (_floatingBall is { IsVisible: true })
        {
            if (_floatingUi.State != FloatingUiState.Ball) _floatingUi.ResetToBall();
            _floatingUi.BeginExpand();
            var ball = _floatingBall;
            _ballOriginBeforeExpand = new Point(ball.Left, ball.Top);
            ballCenter = new Point(
                ball.Left + (ball.ActualWidth > 0 ? ball.ActualWidth : ball.Width) / 2,
                ball.Top + (ball.ActualHeight > 0 ? ball.ActualHeight : ball.Height) / 2);
            var placement = WindowPlacementService.CalculateExpansionPlacement(
                new Rect(ball.Left, ball.Top, ball.ActualWidth > 0 ? ball.ActualWidth : ball.Width,
                    ball.ActualHeight > 0 ? ball.ActualHeight : ball.Height),
                new Size(desiredWidth, desiredHeight),
                WindowPlacementService.GetCurrentWorkAreaDip(ball));
            _mainWindow.Width = Math.Max(_mainWindow.MinWidth, placement.Bounds.Width);
            _mainWindow.Height = Math.Max(_mainWindow.MinHeight, placement.Bounds.Height);
            _mainWindow.Left = placement.Bounds.Left;
            _mainWindow.Top = placement.Bounds.Top;
            _floatingUi.CompleteExpand();
        }
        else if (!_mainWindow.IsVisible)
        {
            if (_floatingUi.State == FloatingUiState.Ball) _floatingUi.ShowWindowDirect();
            _mainWindow.Width = Math.Max(_mainWindow.MinWidth, desiredWidth);
            _mainWindow.Height = Math.Max(_mainWindow.MinHeight, desiredHeight);
        }
        _mainWindow.ApplyDisplayPreferences(Settings);
        _mainWindow.Show();
        _floatingBall?.Hide();
        _mainWindow.PlayRevealAnimation(ballCenter);
        _mainWindow.Activate();
        _mainWindow.Focus();
    }

    internal void OpenSettingsView()
    {
        ShowMainWindow();
        _mainWindow?.OpenSettingsView();
    }

    /// <summary>收起主窗口为悬浮球。</summary>
    internal void CollapseToFloatingBall()
    {
        if (_mainWindow is { IsVisible: true })
        {
            if (_floatingUi.State != FloatingUiState.Window)
            {
                _floatingUi.ResetToBall();
                _floatingUi.ShowWindowDirect();
            }
            _floatingUi.BeginCollapse();
            if (Settings.RememberWindowSize && _mainWindow.WindowState == WindowState.Normal)
            {
                var uiScale = Math.Max(0.01, Settings.UiScale);
                Settings.MainWindowWidth = _mainWindow.ActualWidth / uiScale;
                Settings.MainWindowHeight = _mainWindow.ActualHeight / uiScale;
            }
            if (_ballOriginBeforeExpand is { } originalBall)
            {
                var restored = WindowPlacementService.GetRestoredBallOrigin(originalBall, new Point(_mainWindow.Left, _mainWindow.Top));
                Settings.BallLeft = restored.X;
                Settings.BallTop = restored.Y;
            }
            var targetBallOrigin = _ballOriginBeforeExpand
                ?? (Settings.BallLeft.HasValue && Settings.BallTop.HasValue
                    ? new Point(Settings.BallLeft.Value, Settings.BallTop.Value)
                    : new Point(_mainWindow.Left - 8, _mainWindow.Top - 8));
            var targetBallCenter = targetBallOrigin + new Vector(30, 30);
            _mainWindow.PlayCollapseAnimation(targetBallCenter, () =>
            {
                _mainWindow?.Hide();
                if (Settings.FloatingBallEnabled) ShowFloatingBall();
                if (_floatingUi.State == FloatingUiState.Collapsing) _floatingUi.CompleteCollapse();
                _ballOriginBeforeExpand = null;
                ConfigService.Save(Settings);
            });
            return;
        }
        if (Settings.FloatingBallEnabled) ShowFloatingBall();
        if (_floatingUi.State == FloatingUiState.Collapsing) _floatingUi.CompleteCollapse();
        _ballOriginBeforeExpand = null;
        ConfigService.Save(Settings);
    }

    internal void HideMainWindowToTray()
    {
        if (!Settings.TrayEnabled)
        {
            CollapseToFloatingBall();
            return;
        }
        _mainWindow?.Hide();
        _floatingBall?.Hide();
    }

    /// <summary>显示悬浮球。</summary>
    internal void ShowFloatingBall()
    {
        if (!Settings.FloatingBallEnabled) return;
        if (_floatingBall is null)
        {
            _floatingBall = new FloatingBallWindow();
            _floatingBall.Closed += (_, _) => _floatingBall = null;
        }

        // 恢复上次位置（仅当两端坐标均已记忆且有效；未记忆或为 null/NaN 时由窗口自身回退到默认位置）
        if (Settings.RememberFloatingBallPosition && Settings.BallLeft.HasValue && Settings.BallTop.HasValue &&
            !double.IsNaN(Settings.BallLeft.Value) && !double.IsNaN(Settings.BallTop.Value))
        {
            _floatingBall.Left = Settings.BallLeft.Value;
            _floatingBall.Top = Settings.BallTop.Value;
        }

        _floatingBall.Show();
        _floatingBall.Activate();
    }

    internal void BeginFloatingBallDrag()
    {
        if (_floatingUi.State == FloatingUiState.Ball) _floatingUi.BeginDrag();
    }

    internal void CompleteFloatingBallDrag(Point origin, Size ballSize, Rect workArea)
    {
        var snapped = Settings.SnapFloatingBallToEdge
            ? WindowPlacementService.SnapBallOrigin(origin, ballSize, workArea)
            : new Point(
                Math.Clamp(origin.X, workArea.Left, Math.Max(workArea.Left, workArea.Right - ballSize.Width)),
                Math.Clamp(origin.Y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - ballSize.Height)));
        if (_floatingBall is not null)
        {
            _floatingBall.Left = snapped.X;
            _floatingBall.Top = snapped.Y;
        }
        if (Settings.RememberFloatingBallPosition)
        {
            Settings.BallLeft = snapped.X;
            Settings.BallTop = snapped.Y;
        }
        if (_floatingUi.State == FloatingUiState.Dragging) _floatingUi.CompleteDrag();
        ConfigService.Save(Settings);
    }

    /// <summary>显式退出应用。</summary>
    internal void ExitApp()
    {
        if (IsExiting) return;
        IsExiting = true;
        // 先停热键，避免回调在关闭后触发
        HotkeyService.Stop();
        _trayIcon?.Dispose();
        _mainWindow?.Close();
        _floatingBall?.Close();
        Current.Shutdown();
    }

    private void OpenSettings()
    {
        OpenSettingsView();
    }

    internal void ApplyResidentSettings()
    {
        if (Settings.TrayEnabled)
        {
            if (_trayIcon is null)
            {
                _trayIcon = new TrayIconService(
                    () => Current.Dispatcher.Invoke(ShowMainWindow),
                    () => Current.Dispatcher.Invoke(OpenSettings),
                    () => Current.Dispatcher.Invoke(ExitApp));
                _trayIcon.Show();
            }
        }
        else
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
        }

        if (!Settings.FloatingBallEnabled)
        {
            _floatingBall?.Close();
            _floatingBall = null;
        }
        else if (_mainWindow is not { IsVisible: true })
        {
            ShowFloatingBall();
        }

        if (_mainWindow is not null)
        {
            _mainWindow.Topmost = Settings.AlwaysOnTop;
            _mainWindow.ShowInTaskbar = Settings.ShowInTaskbar;
        }
        ApplyDisplaySettings(Settings);
    }

    internal void ApplyDisplaySettings(AppSettings settings)
    {
        _mainWindow?.ApplyDisplayPreferences(settings);
        _floatingBall?.ApplyDisplayPreferences(settings);
    }

    /// <summary>应用退出时保存配置、释放热键。</summary>
    private void App_OnExit(object sender, ExitEventArgs e)
    {
        if (_systemThemeChangedHandler is not null)
        {
            SystemEvents.UserPreferenceChanged -= _systemThemeChangedHandler;
            _systemThemeChangedHandler = null;
        }
        _singleInstance?.Dispose();
        try
        {
            // 持久化悬浮球位置与历史（历史由 ViewModel 写入 Settings.History）
            ConfigService.Save(Settings);
        }
        catch
        {
            // 忽略退出时保存失败
        }
        HotkeyService.Dispose();
        _trayIcon?.Dispose();
    }
}
