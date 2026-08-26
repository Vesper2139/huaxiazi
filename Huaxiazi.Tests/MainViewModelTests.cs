using Huaxiazi.Models;
using Huaxiazi.ViewModels;
using Huaxiazi.Services;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Huaxiazi.Tests;

/// <summary>
/// 验证 MainViewModel 的「单文本框对照」相关逻辑：
///  - 默认视图为 Original，DisplayText 返回 UserInput；
///  - 设置 OptimizedResult 非空后 ShowResultToggle 为 true，切到 Optimized 时 DisplayText 返回结果、IsReadOnly 为 true；
///  - SetViewModeCommand 可切换视图模式；
///  - OptimizedResult setter 会 raise ShowResultToggle 通知。
/// 不触发任何网络请求（不调用 OptimizeAsync）。
/// </summary>
public class MainViewModelTests : IDisposable
{
    private readonly string _testDataRoot = Path.Combine(Path.GetTempPath(), "HuaxiaziMainVmClass_" + Guid.NewGuid().ToString("N"));

    public MainViewModelTests() => ResetSettings();

    private void ResetSettings() => App.ReplaceSettings(new AppSettings { DataDirectory = _testDataRoot });

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_testDataRoot)) Directory.Delete(_testDataRoot, true); } catch { }
    }
    [Fact]
    public void NewViewModel_DefaultsToPolishModeAndShowsModeSwitcher()
    {
        ResetSettings();
        var root = Path.Combine(Path.GetTempPath(), "HuaxiaziMainVm_" + Guid.NewGuid().ToString("N"));
        try
        {
            var vm = new MainViewModel(new WorkspaceDraftService(root), new ArchiveService(root));
            Assert.Equal(ApplicationMode.Polish, vm.CurrentMode);
            Assert.True(vm.IsPolishMode);
            Assert.True(vm.ShowModeSwitcher);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void SelectModeCommand_SwitchesToPromptMode()
    {
        var vm = new MainViewModel();

        vm.SelectModeCommand.Execute(ApplicationMode.PromptOptimize);

        Assert.Equal(ApplicationMode.PromptOptimize, vm.CurrentMode);
        Assert.False(vm.IsPolishMode);
        Assert.Equal("优化", vm.PrimaryActionText);
    }

    [Fact]
    public void SelectModeCommand_WhileRequestIsBusy_LeavesTheRequestModeUnchanged()
    {
        var vm = new MainViewModel { CurrentMode = ApplicationMode.Polish, IsBusy = true };

        vm.SelectModeCommand.Execute(ApplicationMode.PromptOptimize);

        Assert.Equal(ApplicationMode.Polish, vm.CurrentMode);
    }

    [Fact]
    public void UndoRedo_AreIsolatedPerMode_AndModeSwitchDoesNotPolluteHistory()
    {
        ResetSettings();
        var root = Path.Combine(Path.GetTempPath(), "HuaxiaziMainVm_" + Guid.NewGuid().ToString("N"));
        try
        {
            var vm = new MainViewModel(new WorkspaceDraftService(root), new ArchiveService(root));
            Assert.Equal(ApplicationMode.Polish, vm.CurrentMode);

            // 润色模式：两次输入→清空，润色栈应积累两条撤回
            vm.UserInput = "同一输入";
            vm.ClearInputCommand.Execute(null);
            vm.UserInput = "润色草稿";
            vm.ClearInputCommand.Execute(null);
            Assert.True(vm.CanUndoWorkspace);

            // 切到提示词模式：润色的撤回不得跟随 → 提示词栈为空
            vm.SelectModeCommand.Execute(ApplicationMode.PromptOptimize);
            Assert.Equal(ApplicationMode.PromptOptimize, vm.CurrentMode);
            Assert.False(vm.CanUndoWorkspace);
            Assert.False(vm.CanRedoWorkspace);

            // 提示词模式操作一次
            vm.UserInput = "同一输入";
            vm.ClearInputCommand.Execute(null);
            Assert.True(vm.CanUndoWorkspace);

            // 提示词内撤回：恢复该模式自己的输入，且不跳到润色模式
            vm.UndoWorkspaceCommand.Execute(null);
            Assert.Equal(ApplicationMode.PromptOptimize, vm.CurrentMode);
            Assert.Equal("同一输入", vm.UserInput);

            // 切回润色：润色历史仍在，撤回恢复润色状态且不跳模式
            vm.SelectModeCommand.Execute(ApplicationMode.Polish);
            Assert.True(vm.CanUndoWorkspace);
            vm.UndoWorkspaceCommand.Execute(null);
            Assert.Equal(ApplicationMode.Polish, vm.CurrentMode);
            Assert.Equal("润色草稿", vm.UserInput);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void RegenerateCommand_CanExecute_FollowsInputAndBusy()
    {
        ResetSettings();
        var vm = new MainViewModel();

        Assert.False(vm.RegenerateCommand.CanExecute(null)); // 无输入不可重新生成
        vm.UserInput = "原文";
        Assert.True(vm.RegenerateCommand.CanExecute(null));
        vm.IsBusy = true;
        Assert.False(vm.RegenerateCommand.CanExecute(null)); // 忙时不可
        vm.IsBusy = false;
        Assert.True(vm.RegenerateCommand.CanExecute(null));
        Assert.True(vm.CanRegenerate);
    }

    [Fact]
    public void ModeToggleLabel_Target_ReflectCurrentMode()
    {
        ResetSettings();
        var vm = new MainViewModel { CurrentMode = ApplicationMode.Polish };

        Assert.Equal("润色", vm.ModeToggleLabel);
        Assert.Equal(ApplicationMode.PromptOptimize, vm.ToggleModeTarget);

        vm.SelectModeCommand.Execute(ApplicationMode.PromptOptimize);

        Assert.Equal(ApplicationMode.PromptOptimize, vm.CurrentMode);
        Assert.Equal("提示词", vm.ModeToggleLabel);
        Assert.Equal(ApplicationMode.Polish, vm.ToggleModeTarget);
    }

    [Fact]
    public void ViewToggleLabel_Target_ReflectCurrentView()
    {
        ResetSettings();
        var vm = new MainViewModel { ViewMode = ViewMode.Original };

        Assert.Equal("优化稿", vm.ViewToggleLabel);
        Assert.Equal(ViewMode.Optimized, vm.ViewToggleTarget);

        vm.OptimizedResult = "结果";
        vm.SetViewModeCommand.Execute(ViewMode.Optimized);

        Assert.Equal(ViewMode.Optimized, vm.ViewMode);
        Assert.Equal("原文", vm.ViewToggleLabel);
        Assert.Equal(ViewMode.Original, vm.ViewToggleTarget);
    }

    [Fact]
    public void ViewToggle_OptimizedWithoutResult_IsGuarded()
    {
        ResetSettings();
        var vm = new MainViewModel();

        vm.SetViewModeCommand.Execute(ViewMode.Optimized);

        Assert.Equal(ViewMode.Original, vm.ViewMode); // 无结果时切不到优化稿
    }

    [Fact]
    public void PrimaryActionText_UsesPolishLabelInPolishMode()
    {
        var vm = new MainViewModel { CurrentMode = ApplicationMode.Polish };

        Assert.Equal("润色", vm.PrimaryActionText);
        vm.IsBusy = true;
        Assert.Equal("润色中…", vm.PrimaryActionText);
    }

    [Fact]
    public void IsDisplayTextEmpty_TracksCurrentEditorContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "HuaxiaziEmptyEditor_" + Guid.NewGuid().ToString("N"));
        try
        {
            var vm = new MainViewModel(new WorkspaceDraftService(root), new ArchiveService(root));
            Assert.True(vm.IsDisplayTextEmpty);

            vm.UserInput = "已有文本";

            Assert.False(vm.IsDisplayTextEmpty);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ToggleContextCommand_ChangesContextVisibility()
    {
        var vm = new MainViewModel();
        Assert.False(vm.IsContextExpanded);

        vm.ToggleContextCommand.Execute(null);

        Assert.True(vm.IsContextExpanded);
    }

    [Fact]
    public void DefaultViewMode_IsOriginal_AndDisplayTextEqualsUserInput()
    {
        var vm = new MainViewModel();
        Assert.Equal(ViewMode.Original, vm.ViewMode);
        Assert.Equal(vm.UserInput, vm.DisplayText);
        Assert.False(vm.IsReadOnly);
    }

    [Fact]
    public void SettingOptimizedResult_NonEmpty_ShowResultToggleAndDisplayTextAndReadOnly()
    {
        var vm = new MainViewModel();
        vm.OptimizedResult = "优化后的提示词";

        // 结果非空 -> 分段切换可见
        Assert.True(vm.ShowResultToggle);

        // 仅当切到优化后视图时，DisplayText 才返回结果
        Assert.Equal(vm.UserInput, vm.DisplayText);
        Assert.False(vm.IsReadOnly);

        vm.ViewMode = ViewMode.Optimized;
        Assert.Equal("优化后的提示词", vm.DisplayText);
        Assert.True(vm.IsReadOnly);
    }

    [Fact]
    public void SetViewModeCommand_TogglesViewMode()
    {
        var vm = new MainViewModel { OptimizedResult = "结果" };
        Assert.Equal(ViewMode.Original, vm.ViewMode);

        vm.SetViewModeCommand.Execute(ViewMode.Optimized);
        Assert.Equal(ViewMode.Optimized, vm.ViewMode);

        vm.SetViewModeCommand.Execute(ViewMode.Original);
        Assert.Equal(ViewMode.Original, vm.ViewMode);
    }

    [Fact]
    public void OptimizedResultSetter_RaisesShowResultToggle()
    {
        var vm = new MainViewModel();
        // 初始无结果 -> 分段切换隐藏
        Assert.False(vm.ShowResultToggle);

        vm.OptimizedResult = "结果文本";
        Assert.True(vm.ShowResultToggle);
    }

    [Fact]
    public void SwitchingViewMode_RaisesShowDiffBecauseItsEffectiveValueChanges()
    {
        var vm = new MainViewModel { OptimizedResult = "结果文本", ShowDiff = true };
        var changed = new List<string?>();
        vm.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        vm.ViewMode = ViewMode.Optimized;

        Assert.Contains(nameof(MainViewModel.ShowDiff), changed);
        Assert.True(vm.ShowDiff);
    }

    [Fact]
    public void ReloadPreferences_UsesSavedDiffPreferenceAfterSettingsClose()
    {
        var vm = new MainViewModel { ShowDiff = false };
        App.Settings.ShowDiff = true;

        vm.ReloadPreferences();

        vm.ViewMode = ViewMode.Optimized;
        vm.OptimizedResult = "结果文本";
        Assert.True(vm.ShowDiff);
    }

    [Fact]
    public void OptimizedView_UsesIndependentDiffFlag_AndCanUnlockEditing()
    {
        App.Settings.ShowDiff = false;
        var vm = new MainViewModel { OptimizedResult = "结果文本", ViewMode = ViewMode.Optimized };

        Assert.True(vm.IsReadOnly);
        Assert.False(vm.ShowDiff);

        vm.ShowDiff = true;
        Assert.True(vm.ShowDiff);

        vm.IsEditingResult = true;
        Assert.False(vm.IsReadOnly);
        Assert.False(vm.ShowDiff);
    }

    [Fact]
    public void BeginEditResult_SwitchesToCleanOptimizedEditor()
    {
        var vm = new MainViewModel
        {
            OptimizedResult = "结果文本",
            ViewMode = ViewMode.Original,
            ShowDiff = true
        };

        vm.BeginEditResultCommand.Execute(null);

        Assert.Equal(ViewMode.Optimized, vm.ViewMode);
        Assert.True(vm.IsEditingResult);
        Assert.False(vm.ShowDiff);
        Assert.Equal("结果文本", vm.DisplayText);
    }

    [Fact]
    public void SetViewModeCommand_OnlyAllowsOriginalAndOptimized()
    {
        var vm = new MainViewModel { OptimizedResult = "结果文本" };

        vm.SetViewModeCommand.Execute(ViewMode.Optimized);

        Assert.Equal(ViewMode.Optimized, vm.ViewMode);
    }
}
