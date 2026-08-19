using System;
using System.IO;
using PromptFloat.Models;
using PromptFloat.Services;
using PromptFloat.ViewModels;
using Xunit;

namespace PromptFloat.Tests;

/// <summary>
/// 验证 SettingsViewModel 的脏标（HasChanges）行为。
/// 重点覆盖已知修复项：打开设置窗口（加载）后 HasChanges 必须为 false，
/// 确保“未做任何改动即关闭”不会误触发保存提示。
/// 注：ApiBase / ApiKey / Model 与默认方向、默认深度、快捷键等均由 SettingsViewModel 统一管理，
/// 本测试覆盖其脏标（HasChanges）与保存/取消行为。
/// </summary>
public class SettingsViewModelTests
{
    [Fact]
    public void Constructor_LoadsHistoryImmediatelyInsteadOfShowingAnEmptyPlaceholder()
    {
        var root = Path.Combine(Path.GetTempPath(), "HuaxiaziSettingsVm_" + Guid.NewGuid().ToString("N"));
        try
        {
            var archive = new ArchiveService(root);
            archive.SaveRevision(new ArchiveDraft { OriginalText = "原文", FinalText = "优化稿", Topic = "自动加载" }, DateTimeOffset.UtcNow);

            var vm = new SettingsViewModel(archive);

            Assert.Equal("自动加载", Assert.Single(vm.ArchiveItems).Topic);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void LoadArchiveCommand_PopulatesItemsAndSelectionEnablesRealMutationFlow()
    {
        var root = Path.Combine(Path.GetTempPath(), "HuaxiaziSettingsVm_" + Guid.NewGuid().ToString("N"));
        try
        {
            var archive = new ArchiveService(root);
            var saved = archive.SavePolishRevision(new ArchiveDraft { OriginalText = "待搜索原文", FinalText = "成稿", Topic = "验收主题" }, DateTimeOffset.UtcNow);
            var vm = new SettingsViewModel(archive) { ArchiveSearch = "验收主题" };

            vm.LoadArchiveCommand.Execute(null);

            var item = Assert.Single(vm.ArchiveItems);
            Assert.Equal(saved.Id, item.Id);
            vm.SelectedArchiveItem = item;
            vm.SoftDeleteArchiveCommand.Execute(null);
            Assert.Empty(vm.ArchiveItems);
            vm.ShowDeleted = true;
            vm.LoadArchiveCommand.Execute(null);
            Assert.True(Assert.Single(vm.ArchiveItems).DeletedAt.HasValue);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Constructor_DoesNotMarkHasChanges()
    {
        // 打开设置即从全局配置加载；初始化后不应置脏。
        var vm = new SettingsViewModel();
        Assert.False(vm.HasChanges);
    }

    [Fact]
    public void EditingAField_MarksHasChanges()
    {
        var vm = new SettingsViewModel();
        Assert.False(vm.HasChanges);

        vm.DefaultCategory = PromptCategory.Coding;
        Assert.True(vm.HasChanges);
    }

    [Fact]
    public void MarkClean_ResetsHasChanges()
    {
        var vm = new SettingsViewModel();
        vm.Hotkey = "Ctrl+K";
        Assert.True(vm.HasChanges);

        vm.MarkClean();
        Assert.False(vm.HasChanges);
    }

    [Fact]
    public void SettingFieldToSameLoadedValue_DoesNotMarkHasChanges()
    {
        // 复现“预填同值不置脏”的场景：把字段设为与已载入值相同的内容，
        // SetProperty 应返回 false，从而不误置脏标。
        var vm = new SettingsViewModel();
        var loaded = vm.DefaultCategory;
        vm.DefaultCategory = loaded;

        Assert.False(vm.HasChanges);
    }

    [Fact]
    public void EditingProviderDraft_DoesNotMutateLiveSettingsBeforeSave()
    {
        App.Settings.NormalizeProviderProfiles();
        var original = App.Settings.ProviderProfiles[0].Name;
        var vm = new SettingsViewModel();

        vm.ProviderProfiles[0].Name = "尚未保存的名称";

        Assert.Equal(original, App.Settings.ProviderProfiles[0].Name);
    }

    [Fact]
    public void Save_InvalidHotkey_LeavesLiveSettingsUnchangedAndReportsError()
    {
        App.Settings.Hotkey = "Ctrl+Shift+P";
        var vm = new SettingsViewModel { Hotkey = "invalid" };

        vm.SaveCommand.Execute(null);

        Assert.Equal("Ctrl+Shift+P", App.Settings.Hotkey);
        Assert.False(string.IsNullOrWhiteSpace(vm.ValidationMessage));
        Assert.True(vm.HasChanges);
    }

    [Fact]
    public void TrySave_InvalidHotkey_ReturnsFalse()
    {
        var vm = new SettingsViewModel { Hotkey = "invalid" };

        Assert.False(vm.TrySave());
    }

    [Fact]
    public void EditingHotkeys_ReportsInternalConflictImmediately()
    {
        var vm = new SettingsViewModel();

        vm.QuickPolishHotkey = "Ctrl+Alt+1";
        vm.QuickPromptHotkey = "Ctrl+Alt+1";

        Assert.Contains("快速润色", vm.ValidationMessage);
        Assert.Contains("Prompt 优化", vm.ValidationMessage);
    }

    [Fact]
    public void FloatingBallDependentOptions_FollowMasterSwitch()
    {
        var vm = new SettingsViewModel { FloatingBallEnabled = false };
        Assert.False(vm.IsFloatingBallSettingsEnabled);

        vm.FloatingBallEnabled = true;
        Assert.True(vm.IsFloatingBallSettingsEnabled);
    }

    [Fact]
    public void TrySave_AdvancedParameterOutsideSupportedRange_ShowsValidationInsteadOfSilentlyClamping()
    {
        var vm = new SettingsViewModel();
        vm.SelectedProviderProfile!.Temperature = 3.5;

        var saved = vm.TrySave();

        Assert.False(saved);
        Assert.Contains("Temperature", vm.ValidationMessage);
        Assert.Equal(3.5, vm.SelectedProviderProfile.Temperature);
    }

    [Fact]
    public void TrySave_CloudHttpEndpoint_IsRejectedBeforePersistingUnsafeConfiguration()
    {
        var vm = new SettingsViewModel();
        vm.SelectedProviderProfile!.Type = ProviderType.Cloud;
        vm.SelectedProviderProfile.ApiBase = "http://api.example.test/v1";

        var saved = vm.TrySave();

        Assert.False(saved);
        Assert.Contains("HTTPS", vm.ValidationMessage, StringComparison.OrdinalIgnoreCase);
    }
}
