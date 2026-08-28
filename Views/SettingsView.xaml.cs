using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Threading.Tasks;
using Huaxiazi.Services;
using Huaxiazi.ViewModels;
using Huaxiazi.Models;

namespace Huaxiazi.Views;

public partial class SettingsView : UserControl
{
    private readonly SettingsViewModel _vm = new();
    private readonly UpdateChecker _updateChecker = new();
    private readonly UpdateDownloadService _updateDownloader = new();
    private CancellationTokenSource? _downloadCancellation;
    public bool IsCommitted { get; private set; }
    public bool HasChanges => _vm.HasChanges;
    public event RoutedEventHandler? CloseRequested;

    public SettingsView()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.MarkClean();
    }

    private void ExportLegacyKnowledge_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "SQLite 数据库|*.db", FileName = "huaxiazi-legacy-knowledge.db", AddExtension = true };
        if (dialog.ShowDialog() == true) _vm.ExportLegacyKnowledge(dialog.FileName);
    }

    private void DeleteLegacyKnowledge_OnClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("永久删除旧版知识数据？此操作无法撤销，建议先导出备份。", "话匣子",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
            _vm.DeleteLegacyKnowledge();
    }

    private void HotkeyRecorder_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox recorder) return;
        recorder.Focus();
        recorder.SelectAll();
        e.Handled = true;
    }

    private void HotkeyRecorder_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox recorder) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { Keyboard.ClearFocus(); return; }
        if (key is Key.Back or Key.Delete) { recorder.Text = string.Empty; return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None)
        {
            _vm.ValidationMessageForRecorder("快捷键必须至少包含 Ctrl、Alt、Shift 或 Win 中的一项。");
            return;
        }
        if ((modifiers.HasFlag(ModifierKeys.Alt) && key == Key.F4) ||
            (modifiers.HasFlag(ModifierKeys.Windows) && key is Key.L or Key.D or Key.R or Key.Tab))
        {
            _vm.ValidationMessageForRecorder("该组合由 Windows 保留，请换一个快捷键。");
            return;
        }

        var parts = new System.Collections.Generic.List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        recorder.Text = string.Join("+", parts);
        recorder.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Keyboard.ClearFocus();
    }

    private void HotkeyValueButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value }) return;
        var parts = value.Split('|', 2);
        if (parts.Length != 2 || FindName(parts[0]) is not TextBox recorder) return;
        recorder.Text = parts[1];
        recorder.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    /// <summary>设置页唯一提交路径：退出时自动保存；连通性测试是列表中的可选操作。</summary>
    public async Task<bool> CommitAndCloseAsync()
    {
        if (IsCommitted) return true;
        try
        {
            // 验证未通过时也允许保存草稿；退出不应被网络请求或确认弹窗卡住。
            var saved = await _vm.TrySaveAsync(saveWithoutVerification: true);
            if (!saved) return false;
            IsCommitted = true;
            CloseRequested?.Invoke(this, new RoutedEventArgs());
            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show($"设置自动保存失败：{exception.Message}", "话匣子", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private async void CloseButton_OnClick(object sender, RoutedEventArgs e) => await CommitAndCloseAsync();

    private void PermanentlyClearArchiveButton_OnClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show("永久清空全部历史记录、草稿和回收站？此操作无法撤销。", "清空资料库", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (result == MessageBoxResult.Yes) _vm.PermanentlyClearArchiveCommand.Execute(null);
    }

    private void OpenDataFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_vm.DataDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_vm.DataDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception exception) { _vm.DataStatus = "无法打开数据目录：" + exception.Message; }
    }

    private void ImportJsonButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "话匣子可导入记录 (*.json;*.md;*.txt)|*.json;*.md;*.txt|JSON (*.json)|*.json|Markdown (*.md)|*.md|纯文本 (*.txt)|*.txt"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { _vm.ImportRecord(dialog.FileName); }
        catch (Exception exception) { _vm.DataStatus = "导入失败：" + exception.Message; }
    }

    private void ImportSkinButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "话匣子皮肤包 (*.huaxiaziskin;*.zip)|*.huaxiaziskin;*.zip"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var service = new SkinPackageService(Path.Combine(App.DataRoot, "skins"));
            var manifest = service.Install(dialog.FileName);
            _vm.RegisterImportedSkin(manifest);
        }
        catch (Exception exception)
        {
            MessageBox.Show("皮肤导入失败：" + exception.Message, "话匣子皮肤", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RemoveSkinButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("移除当前导入皮肤并切回默认外观？", "话匣子皮肤", MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { _vm.UninstallSelectedSkin(); }
        catch (Exception exception) { MessageBox.Show("皮肤移除失败：" + exception.Message, "话匣子皮肤", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void RestoreBackupButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "话匣子资料库备份 (*.zip)|*.zip" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (MessageBox.Show("恢复会覆盖同名资料库文件。继续吗？", "恢复资料库", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try { _vm.RestoreBackup(dialog.FileName); }
        catch (Exception exception) { _vm.DataStatus = "恢复失败：" + exception.Message; }
    }

    private void MigrateDataButton_OnClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "选择新的话匣子资料库目录", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        try { _vm.MigrateData(dialog.SelectedPath); }
        catch (Exception exception) { _vm.DataStatus = "迁移失败：" + exception.Message; }
    }

    public void CancelPreview() => _vm.CancelCommand.Execute(null);

    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && Window.GetWindow(this) is { } window) window.DragMove();
    }

    private async void CheckUpdateButton_OnClick(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查更新…";
        try
        {
            DownloadUpdateButton.Visibility = Visibility.Collapsed;
            var result = await _updateChecker.CheckAsync(App.Settings.UpdateCheckUrl);
            UpdateStatusText.Text = string.IsNullOrWhiteSpace(result.ReleaseNotes)
                ? result.Message
                : $"{result.Message}\n{result.ReleaseNotes}";
            if (result.Status == UpdateStatus.UpdateAvailable && !string.IsNullOrWhiteSpace(result.DownloadUrl) && !string.IsNullOrWhiteSpace(result.Sha256))
            {
                DownloadUpdateButton.Tag = result;
                DownloadUpdateButton.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            UpdateStatusText.Text = "检查更新失败";
        }
        finally { CheckUpdateButton.IsEnabled = true; }
    }

    private async void DownloadUpdateButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_downloadCancellation is not null) { _downloadCancellation.Cancel(); return; }
        if (DownloadUpdateButton.Tag is not UpdateCheckResult manifest || string.IsNullOrWhiteSpace(manifest.DownloadUrl) || string.IsNullOrWhiteSpace(manifest.Sha256)) return;
        if (MessageBox.Show($"下载并校验话匣子 {manifest.LatestVersion}？", "下载更新", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var installerPath = Path.Combine(App.DataRoot, "updates", $"Huaxiazi-Setup-{manifest.LatestVersion}.exe");
        _downloadCancellation = new CancellationTokenSource();
        CheckUpdateButton.IsEnabled = false;
        DownloadUpdateButton.Content = "取消下载";
        try
        {
            var progress = new Progress<double>(value => UpdateStatusText.Text = $"正在下载并校验… {value:P0}");
            var result = await _updateDownloader.DownloadAsync(manifest.DownloadUrl, manifest.Sha256, installerPath, progress, _downloadCancellation.Token);
            UpdateStatusText.Text = result.Message;
            if (result.Status != UpdateDownloadStatus.Success || string.IsNullOrWhiteSpace(result.FilePath)) return;
            if (MessageBox.Show("安装包已通过完整性与话匣子发布者签名校验。现在退出并启动安装程序吗？", "安装更新", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            Process.Start(new ProcessStartInfo(result.FilePath) { UseShellExecute = true });
            ((App)Application.Current).ExitApp();
        }
        catch (OperationCanceledException) { UpdateStatusText.Text = "下载已取消。"; }
        catch { UpdateStatusText.Text = "下载更新失败。"; }
        finally
        {
            _downloadCancellation.Dispose();
            _downloadCancellation = null;
            CheckUpdateButton.IsEnabled = true;
            DownloadUpdateButton.Content = "下载新版本";
        }
    }
}
