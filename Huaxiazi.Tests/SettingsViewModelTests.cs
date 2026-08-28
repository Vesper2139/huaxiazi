using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Huaxiazi.ViewModels;
using Xunit;

namespace Huaxiazi.Tests;

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
    public void SettingsNavigation_UsesTaskBasedInformationArchitectureAndStartsWithExpression()
    {
        var vm = new SettingsViewModel(skillCatalogLoader: () => []);

        Assert.Equal(
            ["表达与生成", "模型连接", "外观与窗口", "快捷键", "历史与留存", "数据维护", "关于与更新"],
            vm.Sections);
        Assert.Equal("表达与生成", vm.SelectedSection);
    }

    [Fact]
    public void ExpressionAbilitySection_ConsolidatedEntryOpensOverviewAndReachesSkillManager()
    {
        var vm = new SettingsViewModel(skillCatalogLoader: () => []);

        // “专业 Skill” 独立入口已合并，Skill 管理统一从“表达与生成”进入。
        Assert.DoesNotContain("专业 Skill", vm.Sections);
        Assert.Contains("表达与生成", vm.Sections);

        vm.SelectedSection = "表达与生成";
        Assert.Equal(ExpressionAbilityPane.Overview, vm.ExpressionAbilityPane);
        Assert.True(vm.IsExpressionOverview);

        vm.OpenExpressionSkillsCommand.Execute(null);
        Assert.Equal(ExpressionAbilityPane.Skills, vm.ExpressionAbilityPane);
        Assert.True(vm.IsExpressionSkills);
    }

    [Fact]
    public void SkillManager_ExposesSelectionStateForSafeActions()
    {
        var vm = new SettingsViewModel(skillCatalogLoader: () => []);
        var skill = new AgentSkillRecord
        {
            Id = "demo-skill",
            Candidate = new AgentSkillCandidate
            {
                Name = "demo-skill",
                Description = "demo",
                Status = SkillCompatibilityStatus.Ready
            }
        };

        Assert.False(vm.HasSelectedAgentSkill);
        vm.AgentSkillItems.Add(skill);
        vm.SelectedAgentSkill = skill;

        Assert.True(vm.HasSelectedAgentSkill);
    }

    [Fact]
    public void SkillManager_OnlyShowsDetailsWhenAnItemIsSelectedAndNotBeingEdited()
    {
        var vm = new SettingsViewModel(skillCatalogLoader: () => []);

        Assert.False(vm.IsSkillDetailsVisible);

        var skill = new AgentSkillRecord
        {
            Id = "demo-skill",
            Candidate = new AgentSkillCandidate
            {
                Name = "demo-skill",
                Description = "demo",
                Status = SkillCompatibilityStatus.Ready
            }
        };
        vm.AgentSkillItems.Add(skill);
        vm.SelectedAgentSkill = skill;

        Assert.True(vm.IsSkillDetailsVisible);
        vm.IsSkillEditorOpen = true;
        Assert.False(vm.IsSkillDetailsVisible);
    }

    [Fact]
    public void DisplaySizeChanges_UpdateLiveSummaryAndMarkDraftDirty()
    {
        var vm = new SettingsViewModel();
        vm.HasChanges = false;

        vm.FloatingBallSize = 64;
        vm.FloatingBallOpacity = 0.7;

        Assert.True(vm.HasChanges);
        Assert.Contains("64 px", vm.DisplayPreviewSummary);
        Assert.Contains("70%", vm.DisplayPreviewSummary);
    }

    [Fact]
    public void ExpressionAbility_OpensOnOverviewWithoutStartingSkillScan()
    {
        var loaderStarted = false;
        var vm = new SettingsViewModel(skillCatalogLoader: () =>
        {
            loaderStarted = true;
            return [];
        });

        vm.SelectedSection = "表达与生成";

        Assert.Equal(ExpressionAbilityPane.Overview, vm.ExpressionAbilityPane);
        Assert.False(vm.IsStrategiesLoading);
        Assert.False(loaderStarted);
    }

    [Fact]
    public async Task OpeningSkillManagerStartsBackgroundScanAndReturningToOverviewStaysResponsive()
    {
        using var releaseLoader = new ManualResetEventSlim(false);
        var vm = new SettingsViewModel(skillCatalogLoader: () =>
        {
            releaseLoader.Wait(TimeSpan.FromSeconds(5));
            return [];
        });

        vm.SelectedSection = "表达与生成";
        var stopwatch = Stopwatch.StartNew();
        vm.OpenExpressionSkillsCommand.Execute(null);
        stopwatch.Stop();

        Assert.Equal(ExpressionAbilityPane.Skills, vm.ExpressionAbilityPane);
        Assert.True(vm.IsStrategiesLoading);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250), $"进入 Skill 管理阻塞了 {stopwatch.ElapsedMilliseconds}ms");

        vm.OpenExpressionOverviewCommand.Execute(null);
        Assert.Equal(ExpressionAbilityPane.Overview, vm.ExpressionAbilityPane);

        releaseLoader.Set();
        await vm.StrategiesLoadTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsStrategiesLoading);
    }

    [Fact]
    public async Task LeavingExpressionSectionWhileSkillScanRunsDoesNotBlockNavigation()
    {
        using var releaseLoader = new ManualResetEventSlim(false);
        var item = new AgentSkillRecord
        {
            Id = "slow-skill",
            Candidate = new AgentSkillCandidate
            {
                Name = "slow-skill",
                Description = "Loaded off the UI path",
                Status = SkillCompatibilityStatus.Ready
            }
        };
        var vm = new SettingsViewModel(skillCatalogLoader: () =>
        {
            releaseLoader.Wait(TimeSpan.FromSeconds(5));
            return [item];
        });
        var stopwatch = Stopwatch.StartNew();

        vm.SelectedSection = "表达与生成";
        vm.OpenExpressionSkillsCommand.Execute(null);
        vm.SelectedSection = "历史与留存";
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250), $"导航被 Skill I/O 阻塞了 {stopwatch.ElapsedMilliseconds}ms");
        Assert.Equal("历史与留存", vm.SelectedSection);
        Assert.True(vm.IsStrategiesLoading);

        releaseLoader.Set();
        await vm.StrategiesLoadTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsStrategiesLoading);
        Assert.Equal("slow-skill", Assert.Single(vm.AgentSkillItems).Id);
    }

    [Fact]
    public void TryDiscardChanges_RequiresConfirmationAndKeepsDraftWhenDeclined()
    {
        var vm = new SettingsViewModel { OutputStyle = "正式" };

        Assert.False(vm.TryDiscardChanges(() => false));
        Assert.True(vm.HasChanges);

        Assert.True(vm.TryDiscardChanges(() => true));
        Assert.False(vm.HasChanges);
    }

    [Fact]
    public void InvalidArchiveDate_UsesDedicatedValidationWithoutOverwritingLibraryStatus()
    {
        var vm = new SettingsViewModel();
        var status = vm.DataStatus;
        vm.ArchiveFromText = "not-a-date";

        vm.LoadArchiveCommand.Execute(null);

        Assert.Contains("yyyy-MM-dd", vm.ArchiveValidationMessage);
        Assert.Equal(status, vm.DataStatus);

        vm.ArchiveFromText = string.Empty;
        vm.LoadArchiveCommand.Execute(null);
        Assert.Equal(string.Empty, vm.ArchiveValidationMessage);
    }

    [Fact]
    public void DisablingTray_ReplacesTrayOnlyCloseAndEscapeBehaviors()
    {
        var vm = new SettingsViewModel { CloseBehavior = "Tray", EscapeBehavior = "Tray" };

        vm.TrayEnabled = false;

        Assert.Equal("Hide", vm.CloseBehavior);
        Assert.Equal("Hide", vm.EscapeBehavior);
    }

    [Fact]
    public async Task ToggleSelectedSkill_ImmediatelyPersistsItsEnabledState()
    {
        var original = App.Settings;
        var root = Path.Combine(Path.GetTempPath(), "HuaxiaziSkillSettings_" + Guid.NewGuid().ToString("N"));
        try
        {
            App.ReplaceSettings(new AppSettings { DataDirectory = root });
            var packages = InstallSkillForSettings(root);
            var vm = new SettingsViewModel(new ArchiveService(root), new MemorySecretStore()) { SelectedSection = "表达与生成" };
            vm.OpenExpressionSkillsCommand.Execute(null);
            await vm.StrategiesLoadTask;
            vm.SelectedAgentSkill = Assert.Single(vm.AgentSkillItems);

            vm.ToggleSelectedAgentSkillCommand.Execute(null);

            Assert.False(Assert.Single(packages.ListInstalled()).IsEnabled);
        }
        finally
        {
            App.ReplaceSettings(original);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task TestProviderProfileCommand_IsOptionalAndRunsFromConfigurationList()
    {
        App.ReplaceSettings(new AppSettings());
        var calls = 0;
        var vm = new SettingsViewModel(secretStore: new MemorySecretStore(), connectionTester: (_, _, _) =>
        {
            calls++;
            return Task.FromResult(new ConnectionTestResult(ConnectionTestStatus.Success, "连接正常", HttpStatusCode.OK, 4, "HTTP 200"));
        });
        var profile = Assert.Single(vm.ProviderProfiles);

        await vm.TestProviderProfileCommand.ExecuteAsync(profile);

        Assert.Equal(1, calls);
        Assert.Equal(ConnectionStatusKind.Success, vm.ConnectionStatusKind);
        Assert.Same(profile, vm.SelectedProviderProfile);
    }

    [Fact]
    public async Task RenameSelectedSkill_PersistsAReadableUserFacingName()
    {
        var original = App.Settings;
        var root = Path.Combine(Path.GetTempPath(), "HuaxiaziSkillSettings_" + Guid.NewGuid().ToString("N"));
        try
        {
            App.ReplaceSettings(new AppSettings { DataDirectory = root });
            var packages = InstallSkillForSettings(root);
            var vm = new SettingsViewModel(new ArchiveService(root), new MemorySecretStore()) { SelectedSection = "表达与生成" };
            vm.OpenExpressionSkillsCommand.Execute(null);
            await vm.StrategiesLoadTask;
            vm.SelectedAgentSkill = Assert.Single(vm.AgentSkillItems);
            vm.SkillDisplayName = "日常表达优化";

            vm.SaveSkillDisplayNameCommand.Execute(null);

            Assert.Equal("日常表达优化", Assert.Single(packages.ListInstalled()).EffectiveDisplayName);
            Assert.Equal("text-polisher", Assert.Single(packages.ListInstalled()).Id);
        }
        finally
        {
            App.ReplaceSettings(original);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task EditPresetSkill_CreatesAUserManagedCopyBeforeSavingChanges()
    {
        var original = App.Settings;
        var root = Path.Combine(Path.GetTempPath(), "HuaxiaziSkillSettings_" + Guid.NewGuid().ToString("N"));
        try
        {
            App.ReplaceSettings(new AppSettings { DataDirectory = root });
            var packages = InstallSkillForSettings(root);
            var vm = new SettingsViewModel(new ArchiveService(root), new MemorySecretStore()) { SelectedSection = "表达与生成" };
            vm.OpenExpressionSkillsCommand.Execute(null);
            await vm.StrategiesLoadTask;
            vm.SelectedAgentSkill = Assert.Single(vm.AgentSkillItems);

            vm.EditSelectedAgentSkillCommand.Execute(null);
            vm.SkillEditorText = "Edited professional expression instructions.";
            vm.SaveSkillEditsCommand.Execute(null);

            var custom = Assert.Single(packages.ListInstalled(), item => item.Source == AgentSkillSource.User);
            Assert.Equal("Edited professional expression instructions.", custom.Instructions);
        }
        finally
        {
            App.ReplaceSettings(original);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SkinSelection_IsLoadedAndMarksTheDraftDirty()
    {
        var original = App.Settings;
        try
        {
            App.ReplaceSettings(new AppSettings { SkinId = "MaoDie" });
            var vm = new SettingsViewModel();

            Assert.Equal("MaoDie", vm.SkinId);
            vm.SkinId = "LuoXiaoHei";
            Assert.True(vm.HasChanges);
        }
        finally { App.ReplaceSettings(original); }
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new();
        public int DeleteCalls { get; private set; }
        public void Seed(string id, string value) => _values[id] = value;
        public void Save(string id, string secret) => _values[id] = secret;
        public string? Read(string id) => _values.TryGetValue(id, out var value) ? value : null;
        public bool Exists(string id) => _values.ContainsKey(id);
        public void Delete(string id) { DeleteCalls++; _values.Remove(id); }
    }

    private static AgentSkillPackageService InstallSkillForSettings(string root)
    {
        var preset = Path.Combine(root, "test-presets", "text-polisher");
        Directory.CreateDirectory(preset);
        File.WriteAllText(Path.Combine(preset, "SKILL.md"), """
            ---
            name: text-polisher
            description: External default polishing strategy.
            metadata:
              huaxiazi.modes: "polish"
            ---
            Preserve facts.
            """);
        var packages = new AgentSkillPackageService(Path.Combine(root, "agent-skills"));
        packages.ImportPresets(Path.Combine(root, "test-presets"));
        return packages;
    }

    [Fact]
    public void ExistingSecret_IsRepresentedAsSavedWithoutLoadingPlaintextIntoForm()
    {
        App.ReplaceSettings(new AppSettings());
        var store = new MemorySecretStore();
        store.Seed("provider-default", "must-stay-out-of-view-model");

        var vm = new SettingsViewModel(secretStore: store);

        Assert.True(vm.HasStoredApiKey);
        Assert.Equal(string.Empty, vm.ApiKey);
        Assert.Equal("已安全保存", vm.ApiKeyStateText);
    }

    [Fact]
    public void ExistingSecret_CanBeLoadedOnlyForExplicitEditorReveal()
    {
        App.ReplaceSettings(new AppSettings());
        var store = new MemorySecretStore();
        store.Seed("provider-default", "visible-only-after-request");

        var vm = new SettingsViewModel(secretStore: store);

        Assert.Equal(string.Empty, vm.ApiKey);
        Assert.Equal("visible-only-after-request", vm.GetApiKeyForEditor());
        Assert.Equal(string.Empty, vm.ApiKey);
    }

    [Fact]
    public void SelectingInferenceLevel_UpdatesTheSelectedProfileAndRevealsCustomEditorOnlyOnDemand()
    {
        App.ReplaceSettings(new AppSettings());
        var vm = new SettingsViewModel(secretStore: new MemorySecretStore());

        vm.SelectedInferenceLevel = InferenceLevel.High;
        Assert.Equal(InferenceLevel.High, vm.SelectedProviderProfile!.InferenceLevel);
        Assert.Equal(4096, vm.SelectedProviderProfile.MaxTokens);
        Assert.False(vm.IsCustomInference);

        vm.SelectedInferenceLevel = InferenceLevel.Custom;
        Assert.True(vm.IsCustomInference);
        Assert.Equal(4096, vm.SelectedProviderProfile.MaxTokens);
    }

    [Fact]
    public void ApiKeyDrafts_ArePreservedIndependentlyWhenSwitchingProfiles()
    {
        App.ReplaceSettings(new AppSettings());
        var vm = new SettingsViewModel(secretStore: new MemorySecretStore());
        var first = vm.SelectedProviderProfile!;
        vm.ApiKey = "first-key";
        vm.AddProviderCommand.Execute(null);
        var second = vm.SelectedProviderProfile!;
        vm.ApiKey = "second-key";

        vm.SelectedProviderProfile = first;
        Assert.Equal("first-key", vm.ApiKey);
        vm.SelectedProviderProfile = second;
        Assert.Equal("second-key", vm.ApiKey);
    }

    [Fact]
    public void ClearApiKey_IsExplicitAndDoesNotDeleteStoredSecretBeforeSave()
    {
        App.ReplaceSettings(new AppSettings());
        var store = new MemorySecretStore();
        store.Seed("provider-default", "old-key");
        var vm = new SettingsViewModel(secretStore: store);

        vm.ClearApiKeyCommand.Execute(null);

        Assert.False(vm.HasStoredApiKey);
        Assert.Equal("保存后移除", vm.ApiKeyStateText);
        Assert.Equal(0, store.DeleteCalls);
        Assert.Equal("old-key", store.Read("provider-default"));
    }

    [Fact]
    public void SwitchingProvider_DoesNotReuseThePreviousProvidersStoredKey()
    {
        App.ReplaceSettings(new AppSettings());
        var store = new MemorySecretStore();
        store.Seed("provider-default", "openai-key");
        string? testedKey = "not-called";
        var vm = new SettingsViewModel(secretStore: store, connectionTester: (_, key, _) =>
        {
            testedKey = key;
            return Task.FromResult(new ConnectionTestResult(ConnectionTestStatus.MissingKey,
                "请填写 API Key", null, 0, "状态: MissingKey"));
        });

        vm.SelectedProviderPlatform = ProviderPlatformCatalog.Get(ProviderPlatform.DeepSeek);
        vm.TestConnectionCommand.Execute(null);

        Assert.Null(testedKey);
        Assert.Equal(string.Empty, vm.ApiKey);
        Assert.Equal("保存后移除", vm.ApiKeyStateText);
        Assert.Equal("openai-key", store.Read("provider-default"));
    }

    [Fact]
    public void RemovingProvider_StagesSecretDeletionUntilSettingsAreSaved()
    {
        var settings = new AppSettings
        {
            ProviderProfiles =
            [
                new ProviderProfile { Id = "one", SecretId = "secret-one" },
                new ProviderProfile { Id = "two", SecretId = "secret-two" }
            ],
            ActiveProviderProfileId = "one"
        };
        App.ReplaceSettings(settings);
        var store = new MemorySecretStore();
        store.Seed("secret-one", "key-one");
        var vm = new SettingsViewModel(secretStore: store);

        vm.RemoveProviderCommand.Execute(null);

        Assert.Equal(0, store.DeleteCalls);
        Assert.Equal("key-one", store.Read("secret-one"));
    }

    [Fact]
    public void TrySave_CommitsPendingKeysForEveryProfile()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                App.ReplaceSettings(new AppSettings());
                var store = new MemorySecretStore();
                var vm = new SettingsViewModel(secretStore: store);
                var first = vm.SelectedProviderProfile!;
                vm.ApiKey = "first-key";
                vm.AddProviderCommand.Execute(null);
                var second = vm.SelectedProviderProfile!;
                vm.ApiKey = "second-key";

                Assert.True(vm.TrySave());
                Assert.Equal("first-key", store.Read(first.SecretId));
                Assert.Equal("second-key", store.Read(second.SecretId));
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void TrySave_CommitsExplicitSecretRemoval()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                App.ReplaceSettings(new AppSettings());
                var store = new MemorySecretStore();
                store.Seed("provider-default", "old-key");
                var vm = new SettingsViewModel(secretStore: store);
                vm.ClearApiKeyCommand.Execute(null);

                Assert.True(vm.TrySave());
                Assert.Null(store.Read("provider-default"));
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void TrySave_CommitsSecretRemovalForDeletedProvider()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                App.ReplaceSettings(new AppSettings
                {
                    ProviderProfiles =
                    [
                        new ProviderProfile { Id = "one", SecretId = "secret-one" },
                        new ProviderProfile { Id = "two", SecretId = "secret-two" }
                    ],
                    ActiveProviderProfileId = "one"
                });
                var store = new MemorySecretStore();
                store.Seed("secret-one", "key-one");
                var vm = new SettingsViewModel(secretStore: store);
                vm.RemoveProviderCommand.Execute(null);

                Assert.True(vm.TrySave());
                Assert.Null(store.Read("secret-one"));
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void TrySaveAsync_WhenConnectionValidationFails_KeepsDraftAndDoesNotPersistKey()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                App.ReplaceSettings(new AppSettings());
                var store = new MemorySecretStore();
                var vm = new SettingsViewModel(secretStore: store, connectionTester: (_, _, _) =>
                    Task.FromResult(new ConnectionTestResult(ConnectionTestStatus.AuthFailed,
                        "API Key 无效", HttpStatusCode.Unauthorized, 12, "状态: HTTP 401")));
                vm.ApiKey = "wrong-key";

                var saved = vm.TrySaveAsync().GetAwaiter().GetResult();

                Assert.False(saved);
                Assert.True(vm.CanSaveWithoutVerification);
                Assert.Equal(ConnectionStatusKind.Failure, vm.ConnectionStatusKind);
                Assert.Null(store.Read("provider-default"));
                Assert.Equal("wrong-key", vm.ApiKey);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void TrySaveAsync_WhenConnectionSucceeds_PersistsAndMarksVerified()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                App.ReplaceSettings(new AppSettings());
                var store = new MemorySecretStore();
                var vm = new SettingsViewModel(secretStore: store, connectionTester: (_, key, _) =>
                {
                    Assert.Equal("valid-key", key);
                    return Task.FromResult(new ConnectionTestResult(ConnectionTestStatus.Success,
                        "连接成功", HttpStatusCode.OK, 8, "状态: HTTP 200"));
                });
                vm.ApiKey = "valid-key";

                var saved = vm.TrySaveAsync().GetAwaiter().GetResult();

                Assert.True(saved);
                Assert.False(vm.CanSaveWithoutVerification);
                Assert.Equal("valid-key", store.Read("provider-default"));
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public async Task ConnectionTest_CanRetryAfterFailureAndAcceptsTheLatestSuccess()
    {
        App.ReplaceSettings(new AppSettings());
        var store = new MemorySecretStore();
        var calls = 0;
        var vm = new SettingsViewModel(secretStore: store, connectionTester: (_, _, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? new ConnectionTestResult(ConnectionTestStatus.AuthFailed, "API Key 无效", HttpStatusCode.Unauthorized, 4, "状态: HTTP 401")
                : new ConnectionTestResult(ConnectionTestStatus.Success, "连接成功", HttpStatusCode.OK, 5, "状态: HTTP 200"));
        });
        vm.ApiKey = "retry-key";

        await vm.TestConnectionCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionStatusKind.Failure, vm.ConnectionStatusKind);

        await vm.TestConnectionCommand.ExecuteAsync(null);
        Assert.Equal(ConnectionStatusKind.Success, vm.ConnectionStatusKind);
        Assert.False(vm.CanSaveWithoutVerification);
    }

    [Fact]
    public void TrySaveAsync_ExplicitUnverifiedSave_PersistsDraftWithoutCallingTester()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                App.ReplaceSettings(new AppSettings());
                var store = new MemorySecretStore();
                var testerCalled = false;
                var vm = new SettingsViewModel(secretStore: store, connectionTester: (_, _, _) =>
                {
                    testerCalled = true;
                    throw new InvalidOperationException("不应调用连接测试");
                });
                vm.ApiKey = "offline-key";

                var saved = vm.TrySaveAsync(saveWithoutVerification: true).GetAwaiter().GetResult();

                Assert.True(saved);
                Assert.False(testerCalled);
                Assert.Equal("offline-key", store.Read("provider-default"));
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

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
    public void AvailableModels_And_SelectedModel_FollowSelectedProvider()
    {
        App.ReplaceSettings(new AppSettings());
        var vm = new SettingsViewModel();
        Assert.NotEmpty(vm.ProviderProfiles);
        var profile = vm.ProviderProfiles[0];
        vm.SelectedProviderProfile = profile;

        // 切到 DeepSeek → 自动填充默认模型，可用模型列表联动
        vm.SelectedProviderPlatform = ProviderPlatformCatalog.Get(ProviderPlatform.DeepSeek);
        Assert.Equal("deepseek-v4-flash", profile.Model);
        Assert.Contains(vm.AvailableModels, m => m.ModelId == "deepseek-v4-flash");
        Assert.Contains(vm.AvailableModels, m => m.ModelId == "deepseek-v4-pro");
        Assert.Equal("DeepSeek V4 Flash", vm.SelectedModel?.DisplayName);

        // 切换模型 → 写回 profile.Model（显示名 → 实际 Model ID）
        vm.SelectedModel = vm.AvailableModels.First(m => m.ModelId == "deepseek-v4-pro");
        Assert.Equal("deepseek-v4-pro", profile.Model);
    }

    [Fact]
    public void ModelMapping_LoadsFromProfile_TogglesAndFlushesOnSave()
    {
        // TrySave 深层保存路径涉及 WPF/DPAPI，需 STA 线程
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                App.ReplaceSettings(new AppSettings());
                var vm = new SettingsViewModel();
                var profile = vm.ProviderProfiles[0];
                vm.SelectedProviderProfile = profile;

                Assert.False(vm.EnableModelMapping);
                Assert.Empty(vm.ModelMappingEntries);

                vm.EnableModelMapping = true;
                vm.AddModelMappingEntryCommand.Execute(null);
                vm.ModelMappingEntries[0].Key = "claude-sonnet";
                vm.ModelMappingEntries[0].Value = "deepseek-chat";

                Assert.True(vm.TrySave());
                Assert.True(profile.EnableModelMapping);
                Assert.Equal("deepseek-chat", profile.ModelMapping["claude-sonnet"]);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void ModelMappingEntries_ReloadWhenSwitchingProfiles()
    {
        App.ReplaceSettings(new AppSettings());
        var vm = new SettingsViewModel();
        var first = vm.ProviderProfiles[0];
        vm.SelectedProviderProfile = first;

        vm.EnableModelMapping = true;
        vm.AddModelMappingEntryCommand.Execute(null);
        vm.ModelMappingEntries[0].Key = "alias";
        vm.ModelMappingEntries[0].Value = "real";

        // 复制配置 → 切过去，映射随 Clone 一并保留
        vm.DuplicateProviderCommand.Execute(null);
        var duplicate = vm.SelectedProviderProfile!;
        Assert.NotSame(first, duplicate);
        Assert.True(duplicate.EnableModelMapping);
        Assert.Contains(duplicate.ModelMapping, pair => pair.Key == "alias" && pair.Value == "real");
        Assert.Single(vm.ModelMappingEntries);
    }

    [Fact]
    public void ProviderSearch_FiltersListAndKeepsSelection()
    {
        App.ReplaceSettings(new AppSettings());
        var vm = new SettingsViewModel();

        // 空搜索 → 全量
        vm.ProviderSearchText = string.Empty;
        Assert.Equal(vm.AllProviderPlatforms.Count, vm.ProviderPlatforms.Count);

        // 命中过滤：DeepSeek 保留；未命中的非选中项（Kimi）被排除；选中项 OpenAI 始终保留
        vm.ProviderSearchText = "deepseek";
        Assert.Contains(vm.ProviderPlatforms, p => p.DisplayName == "DeepSeek");
        Assert.Contains(vm.ProviderPlatforms, p => p.DisplayName == "OpenAI"); // 保留选中项
        Assert.DoesNotContain(vm.ProviderPlatforms, p => p.DisplayName == "Kimi（月之暗面）");

        // 清空恢复全量
        vm.ProviderSearchText = "";
        Assert.Equal(vm.AllProviderPlatforms.Count, vm.ProviderPlatforms.Count);
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
    public void CompanionDriverMode_LoadsAsADraftAndDoesNotMutateLiveSettingsBeforeSave()
    {
        App.ReplaceSettings(new AppSettings { CompanionDriverMode = CompanionDriverMode.Local });
        var vm = new SettingsViewModel();

        vm.CompanionDriverMode = CompanionDriverMode.EmotionAssistant;

        Assert.True(vm.HasChanges);
        Assert.Equal(CompanionDriverMode.Local, App.Settings.CompanionDriverMode);
        Assert.Contains(vm.CompanionDriverModes, option => option.Value == CompanionDriverMode.EmotionAssistant);
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
    public void NotifyProviderProfileEdited_RefreshesListAndMarksDraftDirty()
    {
        App.ReplaceSettings(new AppSettings());
        var vm = new SettingsViewModel();
        var profile = vm.ProviderProfiles[0];
        var changed = false;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.FilteredProviderProfiles)) changed = true;
        };

        profile.Name = "新的模型名称";
        vm.NotifyProviderProfileEdited();

        Assert.True(changed);
        Assert.True(vm.HasChanges);
        Assert.Equal("新的模型名称", vm.FilteredProviderProfiles[0].Name);
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
        Assert.Contains("提示词优化", vm.ValidationMessage);
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
    public void HistoryDependentOptions_AreDisabledByIncognitoOrMasterSwitch()
    {
        var vm = new SettingsViewModel { HistoryEnabled = true, IncognitoMode = false };
        Assert.True(vm.CanConfigureHistoryStorage);

        vm.IncognitoMode = true;
        Assert.False(vm.CanConfigureHistoryStorage);
        vm.IncognitoMode = false;
        vm.HistoryEnabled = false;
        Assert.False(vm.CanConfigureHistoryStorage);
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

    [Fact]
    public void TrySave_DataDirectoryPointsToAFile_IsRejectedWithoutChangingLiveSettings()
    {
        var original = App.Settings;
        var root = Path.Combine(Path.GetTempPath(), "HuaxiaziDataPath_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "not-a-directory.txt");
        File.WriteAllText(file, "占位");
        try
        {
            App.ReplaceSettings(new AppSettings { DataDirectory = string.Empty });
            var vm = new SettingsViewModel { DataDirectory = file };

            var saved = vm.TrySave();

            Assert.False(saved);
            Assert.Contains("数据目录", vm.ValidationMessage);
            Assert.Equal(string.Empty, App.Settings.DataDirectory);
        }
        finally
        {
            App.ReplaceSettings(original);
            Directory.Delete(root, true);
        }
    }
}
