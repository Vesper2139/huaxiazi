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
    public void ProviderRoutingSettings_ExposeModesAndTaskOverridesAsEditableSettings()
    {
        var original = App.Settings.Clone();
        var archiveRoot = Path.Combine(AppContext.BaseDirectory, "test-data", "routing-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archiveRoot);
        try
        {
            App.ReplaceSettings(new AppSettings
            {
                ProviderProfiles =
                [
                    new ProviderProfile { Id = "cloud", Name = "云端", Type = ProviderType.Cloud },
                    new ProviderProfile { Id = "local", Name = "本地", Type = ProviderType.Local, Platform = ProviderPlatform.Ollama, ApiBase = "http://localhost:11434/v1" }
                ],
                ActiveProviderProfileId = "cloud",
                ProviderRoutingMode = ProviderRoutingMode.Automatic,
                PolishFastProviderProfileId = "local",
                PromptOptimizeReasoningProviderProfileId = "cloud"
            });
            var vm = new SettingsViewModel(archiveService: new ArchiveService(archiveRoot), skillCatalogLoader: () => []);

            Assert.False(vm.HasChanges);
            Assert.Equal(5, vm.ProviderRoutingModes.Count);
            Assert.Contains(vm.ProviderRoutingModes, option => option.Value == ProviderRoutingMode.Automatic);
            Assert.Equal(ProviderRoutingMode.Automatic, vm.ProviderRoutingMode);
            Assert.True(vm.IsAutomaticRoutingEnabled);
            Assert.Equal("local", vm.PolishFastProviderProfileId);
            Assert.Equal("cloud", vm.PromptOptimizeReasoningProviderProfileId);
            Assert.Contains("启发式建议", vm.ProviderRoutingDescription);
            Assert.Contains("职场沟通", vm.ExpressionPreferenceScenarios.Select(option => option.Value));
            Assert.DoesNotContain("其他", vm.ExpressionPreferenceScenarios.Select(option => option.Value));
            vm.ClearAutomaticTierBindingsCommand.Execute(null);
            Assert.Equal(string.Empty, vm.PolishFastProviderProfileId);
            Assert.Equal(string.Empty, vm.PromptOptimizeReasoningProviderProfileId);
            vm.PolishFastProviderProfileId = "cloud";
            Assert.True(vm.HasChanges);
            vm.ProviderRoutingMode = ProviderRoutingMode.LocalOnly;
            vm.PolishProviderProfileId = "local";

            Assert.True(vm.HasChanges);
            Assert.Contains("不会请求云端", vm.ProviderRoutingDescription);
        }
        finally
        {
            App.ReplaceSettings(original);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(archiveRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AutomaticTierBindings_SaveAndReloadWithTheSettingsViewModel()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var original = App.Settings.Clone();
            var dataRoot = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "AutoRoutingSettings_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataRoot);
            try
            {
                using var configScope = TestHelpers.UseIsolatedConfigDirectory();
                App.ReplaceSettings(new AppSettings
                {
                    DataDirectory = dataRoot,
                    ProviderProfiles =
                    [
                        new ProviderProfile { Id = "fast", Name = "Fast" },
                        new ProviderProfile { Id = "balanced", Name = "Balanced" },
                        new ProviderProfile { Id = "reasoning", Name = "Reasoning" }
                    ],
                    ActiveProviderProfileId = "fast"
                });
                var vm = new SettingsViewModel(archiveService: new ArchiveService(dataRoot), skillCatalogLoader: () => []);
                vm.ProviderRoutingMode = ProviderRoutingMode.Automatic;
                vm.PolishFastProviderProfileId = "fast";
                vm.PolishBalancedProviderProfileId = "balanced";
                vm.PolishReasoningProviderProfileId = "reasoning";
                vm.PromptOptimizeFastProviderProfileId = "balanced";
                vm.PromptOptimizeBalancedProviderProfileId = "reasoning";
                vm.PromptOptimizeReasoningProviderProfileId = "fast";

                Assert.True(vm.TrySave(), vm.ValidationMessage);
                Assert.Equal("fast", App.Settings.PolishFastProviderProfileId);
                Assert.Equal("balanced", App.Settings.PromptOptimizeFastProviderProfileId);

                var reloaded = new SettingsViewModel(archiveService: new ArchiveService(dataRoot), skillCatalogLoader: () => []);
                Assert.Equal(ProviderRoutingMode.Automatic, reloaded.ProviderRoutingMode);
                Assert.Equal("reasoning", reloaded.PolishReasoningProviderProfileId);
                Assert.Equal("fast", reloaded.PromptOptimizeReasoningProviderProfileId);
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                App.ReplaceSettings(original);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true); } catch { }
                TestHelpers.ShutdownCurrentWpfDispatcher();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void OpenAiProtocolSetting_OffersResponsesWhileKeepingChatCompletionsAsDefault()
    {
        var original = App.Settings.Clone();
        var dataRoot = Path.Combine(AppContext.BaseDirectory, "test-data", "openai-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            App.ReplaceSettings(new AppSettings
            {
                ProviderProfiles = [new ProviderProfile { Id = "openai", Platform = ProviderPlatform.OpenAI }],
                ActiveProviderProfileId = "openai"
            });
            var vm = new SettingsViewModel(archiveService: new ArchiveService(dataRoot), skillCatalogLoader: () => []);

            Assert.True(vm.IsOpenAiProtocolSelectorVisible);
            Assert.Equal(ProviderProtocol.OpenAICompatible, vm.SelectedOpenAiProtocol);
            Assert.Contains(vm.OpenAiProtocolOptions, option => option.Value == ProviderProtocol.OpenAIResponses);

            vm.SelectedOpenAiProtocol = ProviderProtocol.OpenAIResponses;

            Assert.Equal(ProviderProtocol.OpenAIResponses, vm.SelectedProviderProfile!.Protocol);
            Assert.True(vm.HasChanges);
        }
        finally
        {
            App.ReplaceSettings(original);
            try { Directory.Delete(dataRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SavingEditedExpressionPreference_ConfirmsOnlyTheSelectedTaskAndScenario()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var original = App.Settings.Clone();
            var dataRoot = Path.Combine(Path.GetTempPath(), "ExpressionPreferenceSave_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataRoot);
            try
            {
                using var configScope = TestHelpers.UseIsolatedConfigDirectory();
                App.ReplaceSettings(new AppSettings { DataDirectory = dataRoot });
                var vm = new SettingsViewModel(skillCatalogLoader: () => []);
                Assert.False(vm.ShareConfirmedPreferencesWithCloud);
                vm.ShareConfirmedPreferencesWithCloud = true;
                vm.ExpressionPreferenceTask = ApplicationMode.Polish;
                vm.PreferredExpressionLength = "concise";
                vm.PreferredExpressionTone = "professional";
                vm.ForbiddenExpressionText = "太客套\n万能套话";

                Assert.True(vm.TrySave(), vm.ValidationMessage);

                var profile = App.Settings.ExpressionPreferenceProfile;
                Assert.True(App.Settings.ShareConfirmedPreferencesWithCloud);
                var polish = profile.TaskPreferences["polish"];
                Assert.True(polish.UserConfirmed);
                Assert.Equal("user-confirmed", polish.Source);
                Assert.Equal(1, polish.Confidence);
                Assert.NotNull(polish.UpdatedAtUtc);
                Assert.Equal(new[] { "太客套", "万能套话" }, polish.ForbiddenExpressions);
                Assert.False(profile.TaskPreferences.ContainsKey("prompt-optimize"));
                Assert.Contains("简洁", new StructuredPreferenceService().BuildInstructions(profile, ApplicationMode.Polish));
                Assert.Equal(string.Empty, new StructuredPreferenceService().BuildInstructions(profile, ApplicationMode.PromptOptimize));

                vm.ExpressionPreferenceScenario = "职场沟通";
                vm.PreferredExpressionLength = "detailed";
                vm.ForbiddenExpressionText = "不合场景表达";
                Assert.True(vm.TrySave(), vm.ValidationMessage);

                profile = App.Settings.ExpressionPreferenceProfile;
                Assert.True(profile.TaskPreferences["polish|职场沟通"].UserConfirmed);
                Assert.Equal(new[] { "不合场景表达" }, profile.TaskPreferences["polish|职场沟通"].ForbiddenExpressions);
                Assert.Contains("简洁", new StructuredPreferenceService().BuildInstructions(profile, ApplicationMode.Polish, "公开发布"));
                var workplaceInstructions = new StructuredPreferenceService().BuildInstructions(profile, ApplicationMode.Polish, "职场沟通");
                Assert.Contains("完整", workplaceInstructions);
                Assert.Contains("不合场景表达", workplaceInstructions);
                Assert.DoesNotContain("太客套", workplaceInstructions);
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                App.ReplaceSettings(original);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true); } catch { }
                TestHelpers.ShutdownCurrentWpfDispatcher();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void PreferencePrivacySettings_RoundTripAndReloadInTheSettingsViewModel()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var original = App.Settings.Clone();
            var dataRoot = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "PreferencePrivacySettings_" + Guid.NewGuid().ToString("N"));
            try
            {
                using var configScope = TestHelpers.UseIsolatedConfigDirectory();
                Directory.CreateDirectory(dataRoot);
                App.ReplaceSettings(new AppSettings { DataDirectory = dataRoot });
                var vm = new SettingsViewModel(skillCatalogLoader: () => []);
                Assert.True(vm.PreferenceLearningEnabled);
                Assert.False(vm.ShareConfirmedPreferencesWithCloud);
                Assert.False(vm.IncognitoMode);

                vm.PreferenceLearningEnabled = false;
                vm.ShareConfirmedPreferencesWithCloud = true;
                vm.IncognitoMode = true;

                Assert.True(vm.TrySave(), vm.ValidationMessage);
                var reloaded = new ConfigService().Load();
                Assert.False(reloaded.PreferenceLearningEnabled);
                Assert.True(reloaded.ShareConfirmedPreferencesWithCloud);
                Assert.True(reloaded.IncognitoMode);

                App.ReplaceSettings(reloaded);
                var reloadedViewModel = new SettingsViewModel(skillCatalogLoader: () => []);
                Assert.False(reloadedViewModel.PreferenceLearningEnabled);
                Assert.True(reloadedViewModel.ShareConfirmedPreferencesWithCloud);
                Assert.True(reloadedViewModel.IncognitoMode);
                Assert.False(reloadedViewModel.CanConfigureHistoryStorage);
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                App.ReplaceSettings(original);
                try { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true); } catch { }
                TestHelpers.ShutdownCurrentWpfDispatcher();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Theory]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(false, true, true, false, true)]
    [InlineData(false, false, true, false, true)]
    public void ChoosingOutputStyle_UsesCurrentDraftLearningAndIncognitoSettings(
        bool savedLearningEnabled,
        bool savedIncognitoMode,
        bool draftLearningEnabled,
        bool draftIncognitoMode,
        bool expectedToRecord)
    {
        var original = App.Settings.Clone();
        try
        {
            App.ReplaceSettings(new AppSettings
            {
                PreferenceLearningEnabled = savedLearningEnabled,
                IncognitoMode = savedIncognitoMode
            });
            var vm = new SettingsViewModel(skillCatalogLoader: () => []);
            vm.PreferenceLearningEnabled = draftLearningEnabled;
            vm.IncognitoMode = draftIncognitoMode;

            vm.OutputStyle = "正式";

            Assert.Equal(expectedToRecord ? 1 : 0, App.Settings.ExpressionPreferenceProfile.StyleChoiceCount);
            Assert.Equal(expectedToRecord, App.Settings.ExpressionPreferenceProfile.StyleChoiceUsage.ContainsKey("正式"));
        }
        finally
        {
            App.ReplaceSettings(original);
        }
    }

    [Fact]
    public void ScopedOutputStyle_IsDraftedPerTaskAndScenarioAndSavedWithoutChangingGlobalFallback()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var original = App.Settings.Clone();
            var dataRoot = Path.Combine(Path.GetTempPath(), "ScopedOutputStyleSave_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataRoot);
            try
            {
                using var configScope = TestHelpers.UseIsolatedConfigDirectory();
                App.ReplaceSettings(new AppSettings { DataDirectory = dataRoot, OutputStyle = "正式" });
                var vm = new SettingsViewModel(skillCatalogLoader: () => []);
                Assert.Equal(string.Empty, vm.ScopedOutputStyle);

                vm.ExpressionPreferenceScenario = "职场沟通";
                vm.ScopedOutputStyle = "克制";
                vm.ExpressionPreferenceScenario = "公开发布";
                Assert.Equal(string.Empty, vm.ScopedOutputStyle);
                vm.ScopedOutputStyle = "简洁";
                vm.ExpressionPreferenceScenario = "职场沟通";
                Assert.Equal("克制", vm.ScopedOutputStyle);

                vm.ExpressionPreferenceTask = ApplicationMode.PromptOptimize;
                Assert.Equal(string.Empty, vm.ScopedOutputStyle);
                vm.ScopedOutputStyle = "专业";
                vm.ExpressionPreferenceScenario = "编程开发";
                vm.ScopedOutputStyle = "亲切";
                Assert.True(vm.TrySave(), vm.ValidationMessage);

                Assert.Equal("正式", App.Settings.OutputStyle);
                Assert.Equal("克制", App.Settings.OutputStyleOverrides["Polish|职场沟通"]);
                Assert.Equal("简洁", App.Settings.OutputStyleOverrides["Polish|公开发布"]);
                Assert.Equal("专业", App.Settings.OutputStyleOverrides["PromptOptimize|"]);
                Assert.Equal("亲切", App.Settings.OutputStyleOverrides["PromptOptimize|编程开发"]);
                var reloaded = App.ConfigService.Load();
                Assert.Equal("正式", reloaded.OutputStyle);
                Assert.Equal("克制", reloaded.OutputStyleOverrides["Polish|职场沟通"]);
                Assert.Equal("亲切", reloaded.OutputStyleOverrides["PromptOptimize|编程开发"]);
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                App.ReplaceSettings(original);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true); } catch { }
                TestHelpers.ShutdownCurrentWpfDispatcher();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void ExportExpressionPreferences_IncludesGlobalAndScopedOutputStyles()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var original = App.Settings.Clone();
            var dataRoot = Path.Combine(Path.GetTempPath(), "ScopedOutputStyleExport_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataRoot);
            var destination = Path.Combine(dataRoot, "preferences.json");
            try
            {
                using var configScope = TestHelpers.UseIsolatedConfigDirectory();
                App.ReplaceSettings(new AppSettings
                {
                    DataDirectory = dataRoot,
                    OutputStyle = "正式",
                    OutputStyleOverrides = new Dictionary<string, string>
                    {
                        ["Polish|职场沟通"] = "克制"
                    }
                });
                var vm = new SettingsViewModel(skillCatalogLoader: () => []);
                vm.ExportExpressionPreferences(destination);

                Assert.Contains("已导出", vm.ValidationMessage);
                using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(destination));
                var styles = json.RootElement.GetProperty("outputStyles");
                Assert.Equal("正式", styles.GetProperty("global").GetString());
                Assert.Equal("克制", styles.GetProperty("overrides").GetProperty("Polish|职场沟通").GetString());
                Assert.DoesNotContain("apiKey", File.ReadAllText(destination), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                App.ReplaceSettings(original);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true); } catch { }
                TestHelpers.ShutdownCurrentWpfDispatcher();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void ResetExpressionPreferences_PersistsFullClearBeforeReportingSuccess()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var original = App.Settings.Clone();
            var dataRoot = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "PreferenceResetData_" + Guid.NewGuid().ToString("N"));
            var backupPath = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "PreferenceResetBackup_" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                using var configScope = TestHelpers.UseIsolatedConfigDirectory();
                Directory.CreateDirectory(dataRoot);
                App.ReplaceSettings(new AppSettings
                {
                    DataDirectory = dataRoot,
                    ExpressionPreferenceProfile = new ExpressionPreferenceProfile
                    {
                        AcceptedCount = 3,
                        ForbiddenExpressions = ["旧规则"],
                        TaskPreferences = new Dictionary<string, ExpressionPreferenceSet>
                        {
                            ["polish"] = new() { UserConfirmed = true, ForbiddenExpressions = ["旧偏好"] }
                        },
                        InteractionSignals = new Dictionary<string, ExpressionInteractionSignalSet>
                        {
                            ["polish|work"] = new()
                            {
                                AcceptedCount = 3,
                                AcceptedRemovedCannedExpressions = new Dictionary<string, int> { ["首先"] = 3 }
                            }
                        }
                    }
                });
                App.ConfigService.Save(App.Settings);
                new DataManagementService().CreateBackup(dataRoot, backupPath, ConfigService.ConfigPath);
                var vm = new SettingsViewModel(archiveService: new ArchiveService(dataRoot), skillCatalogLoader: () => []);

                vm.ResetExpressionPreferencesCommand.Execute(null);

                Assert.Contains("已重置", vm.ValidationMessage);
                Assert.Contains("备份", vm.ValidationMessage);
                var persisted = new ConfigService().Load().ExpressionPreferenceProfile;
                Assert.Empty(persisted.TaskPreferences);
                Assert.Empty(persisted.InteractionSignals);
                Assert.Empty(persisted.ForbiddenExpressions);
                Assert.Equal(0, persisted.AcceptedCount);
                using var backup = System.IO.Compression.ZipFile.OpenRead(backupPath);
                using var backupConfig = new StreamReader(backup.GetEntry("config.json")!.Open());
                Assert.Contains("旧偏好", backupConfig.ReadToEnd(), StringComparison.Ordinal);
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                App.ReplaceSettings(original);
                try { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true); } catch { }
                try { if (File.Exists(backupPath)) File.Delete(backupPath); } catch { }
                TestHelpers.ShutdownCurrentWpfDispatcher();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void ResetExpressionPreferences_RestoresInMemoryProfileWhenPersistenceFails()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var original = App.Settings.Clone();
            var configDirectory = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "PreferenceResetFailure_" + Guid.NewGuid().ToString("N"));
            var dataRoot = Path.Combine(configDirectory, "archive");
            Directory.CreateDirectory(configDirectory);
            Directory.CreateDirectory(dataRoot);
            try
            {
                TestHelpers.RedirectConfigTo(configDirectory);
                Directory.CreateDirectory(Path.Combine(configDirectory, "config.json"));
                var existingProfile = new ExpressionPreferenceProfile
                {
                    AcceptedCount = 7,
                    TaskPreferences = new Dictionary<string, ExpressionPreferenceSet>
                    {
                        ["polish"] = new() { UserConfirmed = true, PreferredTone = "professional" }
                    }
                };
                App.ReplaceSettings(new AppSettings { DataDirectory = dataRoot, ExpressionPreferenceProfile = existingProfile });
                var vm = new SettingsViewModel(archiveService: new ArchiveService(dataRoot), skillCatalogLoader: () => []);

                vm.ResetExpressionPreferencesCommand.Execute(null);

                Assert.Same(existingProfile, App.Settings.ExpressionPreferenceProfile);
                Assert.Equal(7, App.Settings.ExpressionPreferenceProfile.AcceptedCount);
                Assert.Contains("无法", vm.ValidationMessage);
                Assert.DoesNotContain("已重置", vm.ValidationMessage);
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                App.ReplaceSettings(original);
                TestHelpers.ResetConfigToDefault();
                TestHelpers.ShutdownCurrentWpfDispatcher();
                try { Directory.Delete(configDirectory, true); } catch { }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void PreferenceCandidate_LoadsAsDraftAndBecomesActiveOnlyAfterUserSave()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var original = App.Settings.Clone();
            var dataRoot = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "PreferenceCandidateData_" + Guid.NewGuid().ToString("N"));
            try
            {
                using var configScope = TestHelpers.UseIsolatedConfigDirectory();
                Directory.CreateDirectory(dataRoot);
                App.ReplaceSettings(new AppSettings
                {
                    DataDirectory = dataRoot,
                    ExpressionPreferenceProfile = new ExpressionPreferenceProfile
                    {
                        InteractionSignals = new Dictionary<string, ExpressionInteractionSignalSet>
                        {
                            ["polish"] = new()
                            {
                                EditCount = 5,
                                AcceptedCount = 2,
                                AcceptedShortenedOutputs = 3,
                                AcceptedOutputStyles = new Dictionary<string, int> { ["自然"] = 3 },
                                AcceptedRemovedCannedExpressions = new Dictionary<string, int> { ["首先"] = 3 }
                            }
                        }
                    }
                });
                App.ConfigService.Save(App.Settings);
                var vm = new SettingsViewModel(archiveService: new ArchiveService(dataRoot), skillCatalogLoader: () => []);

                Assert.True(vm.HasExpressionPreferenceCandidate);
                Assert.Contains("3 个生成结果经改短后被接受", vm.ExpressionPreferenceCandidateSummary);
                vm.ApplyExpressionPreferenceCandidateCommand.Execute(null);

                Assert.Equal("concise", vm.PreferredExpressionLength);
                var phraseCandidate = Assert.Single(vm.ForbiddenExpressionCandidates);
                Assert.Equal("首先", phraseCandidate.Value);
                vm.ApplyForbiddenExpressionCandidateCommand.Execute(phraseCandidate);
                Assert.Contains("首先", vm.ForbiddenExpressionText, StringComparison.Ordinal);
                vm.ForbiddenExpressionText += Environment.NewLine + "可编辑表达";
                Assert.Empty(App.Settings.ExpressionPreferenceProfile.TaskPreferences);
                Assert.Contains("保存设置后才会确认", vm.ValidationMessage);
                Assert.True(vm.TrySave(), vm.ValidationMessage);

                var confirmed = App.Settings.ExpressionPreferenceProfile.TaskPreferences["polish"];
                Assert.True(confirmed.UserConfirmed);
                Assert.Equal("user-confirmed", confirmed.Source);
                Assert.Equal(1, confirmed.Confidence);
                Assert.Equal(new[] { "首先", "可编辑表达" }, confirmed.ForbiddenExpressions);
                Assert.Null(vm.CurrentExpressionPreferenceCandidate);
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                App.ReplaceSettings(original);
                TestHelpers.ShutdownCurrentWpfDispatcher();
                try { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true); } catch { }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void TonePreferenceCandidate_LoadsAsDraftAndIsIgnoredPerScope()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var original = App.Settings.Clone();
            var dataRoot = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "TonePreferenceCandidateData_" + Guid.NewGuid().ToString("N"));
            try
            {
                using var configScope = TestHelpers.UseIsolatedConfigDirectory();
                Directory.CreateDirectory(dataRoot);
                App.ReplaceSettings(new AppSettings
                {
                    DataDirectory = dataRoot,
                    ExpressionPreferenceProfile = new ExpressionPreferenceProfile
                    {
                        InteractionSignals = new Dictionary<string, ExpressionInteractionSignalSet>
                        {
                            ["polish|职场沟通"] = new()
                            {
                                AcceptedOutputStyles = new Dictionary<string, int> { ["正式"] = 3 }
                            },
                            ["polish|私人沟通"] = new()
                            {
                                AcceptedOutputStyles = new Dictionary<string, int> { ["亲切"] = 3 }
                            }
                        }
                    }
                });
                App.ConfigService.Save(App.Settings);
                var vm = new SettingsViewModel(archiveService: new ArchiveService(dataRoot), skillCatalogLoader: () => []);
                vm.ExpressionPreferenceScenario = "职场沟通";

                Assert.True(vm.HasExpressionToneCandidate);
                Assert.Contains("专业", vm.ExpressionToneCandidateSummary);
                vm.ApplyExpressionToneCandidateCommand.Execute(null);
                Assert.Equal("professional", vm.PreferredExpressionTone);
                Assert.Empty(App.Settings.ExpressionPreferenceProfile.TaskPreferences);
                Assert.True(vm.TrySave(), vm.ValidationMessage);
                Assert.Equal("professional", App.Settings.ExpressionPreferenceProfile.TaskPreferences["polish|职场沟通"].PreferredTone);
                Assert.True(App.Settings.ExpressionPreferenceProfile.TaskPreferences["polish|职场沟通"].UserConfirmed);

                vm.ExpressionPreferenceScenario = "私人沟通";
                Assert.True(vm.HasExpressionToneCandidate);
                vm.IgnoreExpressionToneCandidateCommand.Execute(null);
                Assert.False(vm.HasExpressionToneCandidate);
                Assert.Contains("preferredTone|polish|私人沟通|warm", App.Settings.ExpressionPreferenceProfile.IgnoredSuggestionKeys.Single(), StringComparison.Ordinal);
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                App.ReplaceSettings(original);
                TestHelpers.ShutdownCurrentWpfDispatcher();
                try { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true); } catch { }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        Assert.Null(failure);
    }

    [Fact]
    public void ClearCurrentExpressionPreference_RemovesOnlySelectedTaskScenarioAfterSave()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var original = App.Settings.Clone();
            var dataRoot = Path.Combine(Directory.GetCurrentDirectory(), "out", "test-artifacts", "PreferenceScopeClearData_" + Guid.NewGuid().ToString("N"));
            try
            {
                using var configScope = TestHelpers.UseIsolatedConfigDirectory(
                    Path.Combine(Directory.GetCurrentDirectory(), "out", "test-artifacts", "verification-temp"));
                Directory.CreateDirectory(dataRoot);
                App.ReplaceSettings(new AppSettings
                {
                    DataDirectory = dataRoot,
                    ExpressionPreferenceProfile = new ExpressionPreferenceProfile
                    {
                        TaskPreferences = new Dictionary<string, ExpressionPreferenceSet>
                        {
                            ["polish"] = new() { PreferredLength = "concise", UserConfirmed = true, Source = "user-confirmed", Confidence = 1 },
                            ["polish|职场沟通"] = new() { PreferredLength = "detailed", PreferredTone = "warm", UserConfirmed = true, Source = "user-confirmed", Confidence = 1 }
                        },
                        InteractionSignals = new Dictionary<string, ExpressionInteractionSignalSet>
                        {
                            ["polish|职场沟通"] = new()
                            {
                                AcceptedCount = 2,
                                AcceptedRemovedCannedExpressions = new Dictionary<string, int> { ["首先"] = 3 }
                            }
                        }
                    }
                });
                App.ConfigService.Save(App.Settings);
                var vm = new SettingsViewModel(archiveService: new ArchiveService(dataRoot), skillCatalogLoader: () => []);
                vm.ExpressionPreferenceScenario = "职场沟通";

                vm.ClearCurrentExpressionPreferenceCommand.Execute(null);

                Assert.Equal("balanced", vm.PreferredExpressionLength);
                Assert.Equal("natural", vm.PreferredExpressionTone);
                Assert.Contains("尚未确认", vm.PreferenceMetadataSummary);
                Assert.Contains("polish|职场沟通", App.Settings.ExpressionPreferenceProfile.TaskPreferences.Keys);
                Assert.True(vm.TrySave(), vm.ValidationMessage);

                var saved = new ConfigService().Load();
                Assert.False(saved.ExpressionPreferenceProfile.TaskPreferences.ContainsKey("polish|职场沟通"));
                Assert.Equal("concise", saved.ExpressionPreferenceProfile.TaskPreferences["polish"].PreferredLength);
                Assert.Equal(2, saved.ExpressionPreferenceProfile.InteractionSignals["polish|职场沟通"].AcceptedCount);
                Assert.Equal(3, saved.ExpressionPreferenceProfile.InteractionSignals["polish|职场沟通"].AcceptedRemovedCannedExpressions["首先"]);
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                App.ReplaceSettings(original);
                TestHelpers.ShutdownCurrentWpfDispatcher();
                try { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true); } catch { }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void IgnoringPreferenceCandidate_PersistsLocallyAndSurvivesViewModelReload()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var original = App.Settings.Clone();
            var dataRoot = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "PreferenceIgnoreData_" + Guid.NewGuid().ToString("N"));
            try
            {
                using var configScope = TestHelpers.UseIsolatedConfigDirectory();
                Directory.CreateDirectory(dataRoot);
                App.ReplaceSettings(new AppSettings
                {
                    DataDirectory = dataRoot,
                    ExpressionPreferenceProfile = new ExpressionPreferenceProfile
                    {
                        InteractionSignals = new Dictionary<string, ExpressionInteractionSignalSet>
                        {
                            ["polish"] = new()
                            {
                                AcceptedShortenedOutputs = 2,
                                AcceptedOutputStyles = new Dictionary<string, int> { ["自然"] = 2 },
                                AcceptedRemovedCannedExpressions = new Dictionary<string, int> { ["首先"] = 3 }
                            }
                        }
                    }
                });
                App.ConfigService.Save(App.Settings);
                var archiveService = new ArchiveService(dataRoot);
                var vm = new SettingsViewModel(archiveService: archiveService, skillCatalogLoader: () => []);
                var candidateKey = Assert.IsType<ExpressionPreferenceCandidate>(vm.CurrentExpressionPreferenceCandidate).Key;
                var phraseCandidate = Assert.Single(vm.ForbiddenExpressionCandidates);

                vm.IgnoreExpressionPreferenceCandidateCommand.Execute(null);
                vm.IgnoreForbiddenExpressionCandidateCommand.Execute(phraseCandidate);

                Assert.False(vm.HasExpressionPreferenceCandidate);
                Assert.False(vm.HasForbiddenExpressionCandidates);
                Assert.Contains(candidateKey, new ConfigService().Load().ExpressionPreferenceProfile.IgnoredSuggestionKeys);
                Assert.Contains(phraseCandidate.Key, new ConfigService().Load().ExpressionPreferenceProfile.IgnoredSuggestionKeys);
                var reloadedVm = new SettingsViewModel(archiveService: archiveService, skillCatalogLoader: () => []);
                Assert.False(reloadedVm.HasExpressionPreferenceCandidate);
                Assert.False(reloadedVm.HasForbiddenExpressionCandidates);
                Assert.Contains("忽略", reloadedVm.PreferenceSummary + reloadedVm.ExpressionPreferenceCandidateSummary);
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                App.ReplaceSettings(original);
                TestHelpers.ShutdownCurrentWpfDispatcher();
                try { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true); } catch { }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void SettingsNavigation_UsesTaskBasedInformationArchitectureAndStartsWithExpression()
    {
        var vm = new SettingsViewModel(skillCatalogLoader: () => []);

        var expected = new List<string> { "表达与生成", "模型连接", "本地模型", "外观与窗口", "快捷键", "历史与留存", "数据维护", "关于与更新" };
        Assert.Equal(expected, vm.Sections);
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

    [Fact]
    public void SkinChoices_AreDerivedFromSkinRegistryInsteadOfDuplicatedIds()
    {
        var vm = new SettingsViewModel(skillCatalogLoader: () => []);

        Assert.Contains(vm.SpecialSkins, option => option.Value == "default");
        Assert.Contains(vm.SpecialSkins, option => option.Value == "LuoXiaoHei");
        Assert.Contains(vm.SpecialSkins, option => option.Value == "MaoDie");
    }

    [Fact]
    public void SkinChoices_ExposeBuiltInPreviewResources()
    {
        var vm = new SettingsViewModel(skillCatalogLoader: () => []);

        var luo = Assert.Single(vm.SpecialSkins, option => option.Value == "LuoXiaoHei");
        Assert.Equal("pack://application:,,,/Huaxiazi;component/Resources/Skins/LuoXiaoHei/preview.png", luo.PreviewSource);
    }

    [Fact]
    public void SkinChoices_UseEachSkinOwnPreviewPalette()
    {
        var vm = new SettingsViewModel(skillCatalogLoader: () => []);

        var luo = Assert.Single(vm.SpecialSkins, option => option.Value == "LuoXiaoHei");
        var mao = Assert.Single(vm.SpecialSkins, option => option.Value == "MaoDie");

        Assert.NotEqual(luo.Palette.Surface, mao.Palette.Surface);
        Assert.NotEqual(luo.Palette.Accent, mao.Palette.Accent);
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
    public void SelectedInferenceDescriptionSeparatesPresetValuesFromModelReasoningCapabilities()
    {
        var original = App.Settings.Clone();
        var dataRoot = Path.Combine(AppContext.BaseDirectory, "test-data", "inference-description-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            App.ReplaceSettings(new AppSettings
            {
                ProviderProfiles =
                [
                    new ProviderProfile
                    {
                        Id = "mimo-inference",
                        Platform = ProviderPlatform.MiMo,
                        Protocol = ProviderProtocol.OpenAICompatible,
                        Model = "mimo-v2.6-pro",
                        InferenceLevel = InferenceLevel.Low
                    }
                ],
                ActiveProviderProfileId = "mimo-inference"
            });
            var vm = new SettingsViewModel(archiveService: new ArchiveService(dataRoot), secretStore: new MemorySecretStore());

            Assert.Contains("temperature=0.2", vm.SelectedInferenceDescription);
            Assert.Contains("top_p=0.8", vm.SelectedInferenceDescription);
            Assert.Contains("max_tokens=1024", vm.SelectedInferenceDescription);
            Assert.Contains("具体传参与推理行为以当前模型能力说明为准", vm.SelectedInferenceDescription);
            Assert.Contains("Low 档关闭 thinking", vm.SelectedProviderCapabilitySummary);

            vm.SelectedInferenceLevel = InferenceLevel.High;

            Assert.Contains("temperature=0.3", vm.SelectedInferenceDescription);
            Assert.Contains("top_p=0.95", vm.SelectedInferenceDescription);
            Assert.Contains("max_tokens=4096", vm.SelectedInferenceDescription);
            Assert.Contains("thinking 模式下 temperature/top_p 不生效", vm.SelectedProviderCapabilitySummary);

            vm.SelectedProviderProfile!.Platform = ProviderPlatform.OpenAI;
            vm.SelectedProviderProfile.Protocol = ProviderProtocol.OpenAICompatible;
            vm.ModelId = "gpt-6-astra";

            Assert.Contains("temperature / top_p 使用模型默认值", vm.SelectedProviderCapabilitySummary);
            Assert.Contains("reasoning_effort=high", vm.SelectedProviderCapabilitySummary);
        }
        finally
        {
            App.ReplaceSettings(original);
            try { Directory.Delete(dataRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ExperimentalOllamaProfileDoesNotExposeMachineSpecificBudgetPreset()
    {
        var original = App.Settings.Clone();
        var dataRoot = Path.Combine(AppContext.BaseDirectory, "test-data", "qwen3-medium-preset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            App.ReplaceSettings(new AppSettings
            {
                ProviderProfiles =
                [
                    new ProviderProfile
                    {
                        Id = "ollama-qwen3-4b",
                        Type = ProviderType.Local,
                        Platform = ProviderPlatform.Ollama,
                        Protocol = ProviderProtocol.OpenAICompatible,
                        ApiBase = "http://127.0.0.1:11434/v1",
                        Model = "qwen3:4b",
                        InferenceLevel = InferenceLevel.Medium,
                        MaxTokens = 2048
                    }
                ],
                ActiveProviderProfileId = "ollama-qwen3-4b"
            });
            var vm = new SettingsViewModel(archiveService: new ArchiveService(dataRoot), secretStore: new MemorySecretStore());

            Assert.Equal(2048, vm.SelectedProviderProfile!.MaxTokens);
            Assert.Contains("max_tokens=2048", vm.SelectedInferenceDescription);
            Assert.DoesNotContain("内部合成短文本诊断", vm.SelectedInferenceDescription);

            vm.ModelId = "qwen3:8b";
            Assert.DoesNotContain("内部合成短文本诊断", vm.SelectedInferenceDescription);
            Assert.Equal(2048, vm.SelectedProviderProfile.MaxTokens);

            vm.ModelId = "qwen3:4b";
            vm.ApiBaseInput = "http://127.0.0.1:11435/v1";
            Assert.DoesNotContain("内部合成短文本诊断", vm.SelectedInferenceDescription);
            vm.ApiBaseInput = "http://127.0.0.1:11434/v1";
            Assert.Equal(2048, vm.SelectedProviderProfile.MaxTokens);
            Assert.DoesNotContain("内部合成短文本诊断", vm.SelectedInferenceDescription);
            Assert.Equal(2048, vm.SelectedProviderProfile.MaxTokens);
        }
        finally
        {
            App.ReplaceSettings(original);
            try { Directory.Delete(dataRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ExperimentalOllamaSavedBudgetSurvivesSettingsSaveAndReloadWithoutPreset()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var original = App.Settings.Clone();
            var dataRoot = Path.Combine(Path.GetTempPath(), "Qwen3PresetPersistence_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataRoot);
            try
            {
                using var configScope = TestHelpers.UseIsolatedConfigDirectory();
                App.ReplaceSettings(new AppSettings
                {
                    DataDirectory = dataRoot,
                    ProviderProfiles =
                    [
                        new ProviderProfile
                        {
                            Id = "ollama-qwen3-4b",
                            Type = ProviderType.Local,
                            Platform = ProviderPlatform.Ollama,
                            Protocol = ProviderProtocol.OpenAICompatible,
                            ApiBase = "http://127.0.0.1:11434/v1",
                            Model = "qwen3:4b",
                            InferenceLevel = InferenceLevel.Medium,
                            MaxTokens = 4096
                        }
                    ],
                    ActiveProviderProfileId = "ollama-qwen3-4b"
                });
                var vm = new SettingsViewModel(
                    archiveService: new ArchiveService(dataRoot),
                    secretStore: new MemorySecretStore());

                Assert.True(vm.TrySave(), vm.ValidationMessage);
                var untouched = new ConfigService().Load();
                Assert.Equal(4096, Assert.Single(untouched.ProviderProfiles).MaxTokens);

                var reloaded = new ConfigService().Load();
                var profile = Assert.Single(reloaded.ProviderProfiles);
                Assert.Equal("qwen3:4b", profile.Model);
                Assert.Equal(InferenceLevel.Medium, profile.InferenceLevel);
                Assert.Equal(4096, profile.MaxTokens);
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                App.ReplaceSettings(original);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true); } catch { }
                TestHelpers.ShutdownCurrentWpfDispatcher();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void ProviderCapabilitySummary_ExplainsExactModelParameterBehavior()
    {
        App.ReplaceSettings(new AppSettings());
        var vm = new SettingsViewModel(secretStore: new MemorySecretStore());
        var profile = vm.SelectedProviderProfile!;
        profile.Platform = ProviderPlatform.OpenAI;
        profile.Protocol = ProviderProtocol.OpenAICompatible;
        vm.ModelId = "gpt-6-astra";

        Assert.Contains("temperature / top_p", vm.SelectedProviderCapabilitySummary);
        Assert.Contains("reasoning_effort", vm.SelectedProviderCapabilitySummary);
        Assert.Contains("max_completion_tokens", vm.SelectedProviderCapabilitySummary);
        Assert.Contains("原生 JSON Schema", vm.SelectedProviderCapabilitySummary);

        vm.SelectedInferenceLevel = InferenceLevel.Custom;
        Assert.Contains("自定义档使用模型默认推理强度", vm.SelectedProviderCapabilitySummary);

        vm.ModelId = "gpt-6-astra-preview";
        Assert.Contains("尚未核验", vm.SelectedProviderCapabilitySummary);
        Assert.Contains("不发送原生约束", vm.SelectedProviderCapabilitySummary);
        Assert.Contains("本地校验", vm.SelectedProviderCapabilitySummary);
    }

    [Fact]
    public void ProviderCapabilitySummary_ExplainsAnthropicPostOpus46ParameterBehavior()
    {
        var original = App.Settings.Clone();
        try
        {
            App.ReplaceSettings(new AppSettings
            {
                ProviderProfiles =
                [
                    new ProviderProfile
                    {
                        Id = "anthropic-capability",
                        Platform = ProviderPlatform.Anthropic,
                        Protocol = ProviderProtocol.AnthropicMessages,
                        ApiBase = "https://api.anthropic.com",
                        Model = "claude-opus-5-5"
                    }
                ],
                ActiveProviderProfileId = "anthropic-capability"
            });
            var vm = new SettingsViewModel(secretStore: new MemorySecretStore());

            Assert.Contains("已核验 claude-opus-5-5", vm.SelectedProviderCapabilitySummary);
            Assert.Contains("temperature / top_p 使用模型默认值", vm.SelectedProviderCapabilitySummary);
            Assert.Contains("output_config.effort", vm.SelectedProviderCapabilitySummary);
        }
        finally
        {
            App.ReplaceSettings(original);
        }
    }

    [Fact]
    public void ProviderCapabilitySummary_ExplainsKimiK3EffortAndFixedSampling()
    {
        var original = App.Settings.Clone();
        try
        {
            App.ReplaceSettings(new AppSettings
            {
                ProviderProfiles =
                [
                    new ProviderProfile
                    {
                        Id = "kimi-capability",
                        Platform = ProviderPlatform.Kimi,
                        Protocol = ProviderProtocol.OpenAICompatible,
                        ApiBase = "https://api.moonshot.cn/v1",
                        Model = "kimi-k3",
                        InferenceLevel = InferenceLevel.High
                    }
                ],
                ActiveProviderProfileId = "kimi-capability"
            });
            var vm = new SettingsViewModel(secretStore: new MemorySecretStore());

            Assert.Contains("kimi-k3", vm.SelectedProviderCapabilitySummary);
            Assert.Contains("temperature=1.0 / top_p=0.95 固定", vm.SelectedProviderCapabilitySummary);
            Assert.Contains("reasoning_effort=max", vm.SelectedProviderCapabilitySummary);

            vm.SelectedInferenceLevel = InferenceLevel.Custom;
            Assert.Contains("服务默认推理级别（max）", vm.SelectedProviderCapabilitySummary);
        }
        finally
        {
            App.ReplaceSettings(original);
        }
    }

    [Fact]
    public void ProviderCapabilitySummary_ExplainsZhipuGlm53EffortAndJsonMode()
    {
        var original = App.Settings.Clone();
        try
        {
            App.ReplaceSettings(new AppSettings
            {
                ProviderProfiles =
                [
                    new ProviderProfile
                    {
                        Id = "zhipu-capability",
                        Platform = ProviderPlatform.Zhipu,
                        Protocol = ProviderProtocol.OpenAICompatible,
                        ApiBase = "https://open.bigmodel.cn/api/paas/v4",
                        Model = "glm-5.3",
                        InferenceLevel = InferenceLevel.High
                    }
                ],
                ActiveProviderProfileId = "zhipu-capability"
            });
            var vm = new SettingsViewModel(secretStore: new MemorySecretStore());

            Assert.Contains("reasoning_effort=max", vm.SelectedProviderCapabilitySummary);
            Assert.Contains("json_object", vm.SelectedProviderCapabilitySummary);
            Assert.Contains("Schema 由应用侧校验", vm.SelectedProviderCapabilitySummary);

            vm.ModelId = "glm-5.2";
            vm.SelectedInferenceLevel = InferenceLevel.Low;
            Assert.Contains("reasoning_effort=none（关闭思考）", vm.SelectedProviderCapabilitySummary);
        }
        finally
        {
            App.ReplaceSettings(original);
        }
    }

    [Fact]
    public void ProviderCapabilitySummary_ExplainsSparkJsonModeAndSampling()
    {
        var original = App.Settings.Clone();
        var dataRoot = Path.Combine(AppContext.BaseDirectory, "test-data", "spark-capability-summary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            App.ReplaceSettings(new AppSettings
            {
                ProviderProfiles =
                [
                    new ProviderProfile
                    {
                        Id = "spark-capability",
                        Platform = ProviderPlatform.Spark,
                        Protocol = ProviderProtocol.OpenAICompatible,
                        ApiBase = "https://spark-api-open.xf-yun.com/v1",
                        Model = "generalv3.5"
                    }
                ],
                ActiveProviderProfileId = "spark-capability"
            });
            var vm = new SettingsViewModel(new ArchiveService(dataRoot), new MemorySecretStore());

            Assert.Contains("temperature 限制在 0–2", vm.SelectedProviderCapabilitySummary);
            Assert.Contains("top_p 限制在 (0,1]", vm.SelectedProviderCapabilitySummary);
            Assert.Contains("max_tokens 上限为 8192", vm.SelectedProviderCapabilitySummary);
            Assert.Contains("json_object", vm.SelectedProviderCapabilitySummary);
            Assert.Contains("Schema 由应用侧校验", vm.SelectedProviderCapabilitySummary);
        }
        finally
        {
            App.ReplaceSettings(original);
            try { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true); } catch { }
        }
    }

    [Fact]
    public void ProviderCapabilitySummary_ExplainsGroqStrictStructuredOutputAndEffort()
    {
        var original = App.Settings.Clone();
        try
        {
            App.ReplaceSettings(new AppSettings
            {
                ProviderProfiles =
                [
                    new ProviderProfile
                    {
                        Id = "groq-capability",
                        Platform = ProviderPlatform.Groq,
                        Protocol = ProviderProtocol.OpenAICompatible,
                        ApiBase = "https://api.groq.com/openai/v1",
                        Model = "openai/gpt-oss-120b",
                        InferenceLevel = InferenceLevel.High
                    }
                ],
                ActiveProviderProfileId = "groq-capability"
            });
            var vm = new SettingsViewModel(secretStore: new MemorySecretStore());

            Assert.Contains("reasoning_effort=high", vm.SelectedProviderCapabilitySummary);
            Assert.Contains("严格 JSON Schema", vm.SelectedProviderCapabilitySummary);
            Assert.Contains("temperature/top_p 按设置发送", vm.SelectedProviderCapabilitySummary);
        }
        finally
        {
            App.ReplaceSettings(original);
        }
    }

    [Fact]
    public void ProviderCapabilitySummary_ExplainsGemini3SamplingAndThinkingMapping()
    {
        var original = App.Settings.Clone();
        try
        {
            App.ReplaceSettings(new AppSettings
            {
                ProviderProfiles =
                [
                    new ProviderProfile
                    {
                        Id = "gemini-capability",
                        Platform = ProviderPlatform.Gemini,
                        Protocol = ProviderProtocol.GeminiGenerateContent,
                        ApiBase = "https://generativelanguage.googleapis.com/v1beta",
                        Model = "gemini-3.8-flash",
                        InferenceLevel = InferenceLevel.High
                    }
                ],
                ActiveProviderProfileId = "gemini-capability"
            });
            var vm = new SettingsViewModel(secretStore: new MemorySecretStore());

            Assert.Contains("已核验 gemini-3.8-flash", vm.SelectedProviderCapabilitySummary);
            Assert.Contains("temperature / top_p 使用模型默认值", vm.SelectedProviderCapabilitySummary);
            Assert.Contains("generationConfig.thinkingConfig.thinkingLevel", vm.SelectedProviderCapabilitySummary);
        }
        finally
        {
            App.ReplaceSettings(original);
        }
    }

    [Fact]
    public void ProviderCapabilitySummary_ExplainsCurrentDeepSeekThinkingControls()
    {
        var original = App.Settings.Clone();
        try
        {
            App.ReplaceSettings(new AppSettings
            {
                ProviderProfiles =
                [
                    new ProviderProfile
                    {
                        Id = "deepseek-capability",
                        Platform = ProviderPlatform.DeepSeek,
                        Protocol = ProviderProtocol.OpenAICompatible,
                        ApiBase = "https://api.deepseek.com/v1",
                        Model = "deepseek-flash",
                        InferenceLevel = InferenceLevel.Medium
                    }
                ],
                ActiveProviderProfileId = "deepseek-capability"
            });
            var vm = new SettingsViewModel(secretStore: new MemorySecretStore());

            Assert.Contains("思考模式已启用", vm.SelectedProviderCapabilitySummary);
            Assert.Contains("medium 映射为 high", vm.SelectedProviderCapabilitySummary);
            Assert.Contains("temperature 不生效", vm.SelectedProviderCapabilitySummary);
            Assert.Contains("top_p 按 0.95–1.0 范围发送", vm.SelectedProviderCapabilitySummary);
        }
        finally
        {
            App.ReplaceSettings(original);
        }
    }

    [Fact]
    public void ProviderCapabilitySummary_ExplainsQwen38ReasoningMapping()
    {
        var original = App.Settings.Clone();
        try
        {
            App.ReplaceSettings(new AppSettings
            {
                ProviderProfiles =
                [
                    new ProviderProfile
                    {
                        Id = "qwen-capability",
                        Platform = ProviderPlatform.Qwen,
                        Protocol = ProviderProtocol.OpenAICompatible,
                        ApiBase = "https://dashscope.aliyuncs.com/compatible-mode/v1",
                        Model = "qwen3.8-flash",
                        InferenceLevel = InferenceLevel.High
                    }
                ],
                ActiveProviderProfileId = "qwen-capability"
            });
            var vm = new SettingsViewModel(secretStore: new MemorySecretStore());

            Assert.Contains("阿里云百炼 qwen3.8-flash", vm.SelectedProviderCapabilitySummary);
            Assert.Contains("reasoning_effort=xhigh", vm.SelectedProviderCapabilitySummary);
            Assert.Contains("不发送 thinking_budget", vm.SelectedProviderCapabilitySummary);
        }
        finally
        {
            App.ReplaceSettings(original);
        }
    }

    [Fact]
    public void ProviderCapabilitySummary_ExplainsDoubaoSeed20SamplingAndReasoning()
    {
        var original = App.Settings.Clone();
        try
        {
            App.ReplaceSettings(new AppSettings
            {
                ProviderProfiles =
                [
                    new ProviderProfile
                    {
                        Id = "doubao-capability",
                        Platform = ProviderPlatform.Doubao,
                        Protocol = ProviderProtocol.OpenAICompatible,
                        ApiBase = "https://ark.cn-beijing.volces.com/api/v3",
                        Model = "doubao-seed-2-0-lite-260428",
                        InferenceLevel = InferenceLevel.High
                    }
                ],
                ActiveProviderProfileId = "doubao-capability"
            });
            var vm = new SettingsViewModel(secretStore: new MemorySecretStore());

            Assert.Contains("reasoning_effort=high", vm.SelectedProviderCapabilitySummary);
            Assert.Contains("temperature/top_p 按设置发送", vm.SelectedProviderCapabilitySummary);
        }
        finally
        {
            App.ReplaceSettings(original);
        }
    }

    [Fact]
    public void ProviderCapabilitySummary_UsesResolvedModelFromMapping()
    {
        var original = App.Settings.Clone();
        var dataRoot = Path.Combine(AppContext.BaseDirectory, "test-data", "openai-capability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            App.ReplaceSettings(new AppSettings());
            var vm = new SettingsViewModel(new ArchiveService(dataRoot), new MemorySecretStore());
            var profile = vm.SelectedProviderProfile!;
            profile.Platform = ProviderPlatform.OpenAI;
            profile.Protocol = ProviderProtocol.OpenAICompatible;
            profile.Model = "preferred";
            profile.EnableModelMapping = true;
            vm.ModelMappingEntries.Add(new ModelMappingEntry { Key = "preferred", Value = "o3-2025-04-16" });

            Assert.Contains("reasoning_effort", vm.SelectedProviderCapabilitySummary);
            Assert.Contains("当前官方资料没有明确列出", vm.SelectedProviderCapabilitySummary);
            Assert.Contains("已保存的采样值不生效", vm.SelectedProviderCapabilitySummary);
        }
        finally
        {
            App.ReplaceSettings(original);
            try { Directory.Delete(dataRoot, recursive: true); } catch { }
        }
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
                using var configScope = TestHelpers.UseIsolatedConfigDirectory();
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
            finally { TestHelpers.ShutdownCurrentWpfDispatcher(); }
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
                using var configScope = TestHelpers.UseIsolatedConfigDirectory();
                App.ReplaceSettings(new AppSettings());
                var store = new MemorySecretStore();
                store.Seed("provider-default", "old-key");
                var vm = new SettingsViewModel(secretStore: store);
                vm.ClearApiKeyCommand.Execute(null);

                Assert.True(vm.TrySave());
                Assert.Null(store.Read("provider-default"));
            }
            catch (Exception exception) { failure = exception; }
            finally { TestHelpers.ShutdownCurrentWpfDispatcher(); }
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
                using var configScope = TestHelpers.UseIsolatedConfigDirectory();
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
            finally { TestHelpers.ShutdownCurrentWpfDispatcher(); }
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
            finally { TestHelpers.ShutdownCurrentWpfDispatcher(); }
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
                using var configScope = TestHelpers.UseIsolatedConfigDirectory();
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
            finally { TestHelpers.ShutdownCurrentWpfDispatcher(); }
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
                using var configScope = TestHelpers.UseIsolatedConfigDirectory();
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
            finally { TestHelpers.ShutdownCurrentWpfDispatcher(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void Constructor_LoadsHistoryImmediatelyInsteadOfShowingAnEmptyPlaceholder()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-data", "SettingsVm_" + Guid.NewGuid().ToString("N"));
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
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-data", "SettingsVm_" + Guid.NewGuid().ToString("N"));
        try
        {
            App.ReplaceSettings(new AppSettings());
            var vm = new SettingsViewModel(archiveService: new ArchiveService(root));
            Assert.NotEmpty(vm.ProviderProfiles);
            var profile = vm.ProviderProfiles[0];
            vm.SelectedProviderProfile = profile;

            // 切到 DeepSeek → 自动填充默认模型，可用模型列表联动
            vm.SelectedProviderPlatform = ProviderPlatformCatalog.Get(ProviderPlatform.DeepSeek);
            Assert.Equal("deepseek-flash", profile.Model);
            Assert.Contains(vm.AvailableModels, m => m.ModelId == "deepseek-flash");
            Assert.Contains(vm.AvailableModels, m => m.ModelId == "deepseek-v4-pro");
            Assert.Equal("DeepSeek V4.1 Flash（当前）", vm.SelectedModel?.DisplayName);

            // 切换模型 → 写回 profile.Model（显示名 → 实际 Model ID）
            vm.SelectedModel = vm.AvailableModels.First(m => m.ModelId == "deepseek-v4-pro");
            Assert.Equal("deepseek-v4-pro", profile.Model);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
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
                using var configScope = TestHelpers.UseIsolatedConfigDirectory();
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
            finally { TestHelpers.ShutdownCurrentWpfDispatcher(); }
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
    public void TrySave_LocalGenerationDiagnosticsOptInPersistsWithoutChangingIncognitoSetting()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var original = App.Settings.Clone();
            try
            {
                using var configScope = TestHelpers.UseIsolatedConfigDirectory();
                App.ReplaceSettings(new AppSettings { IncognitoMode = false });
                var vm = new SettingsViewModel { LocalGenerationDiagnosticsEnabled = true };

                Assert.True(vm.TrySave(), vm.ValidationMessage);
                Assert.True(new ConfigService().Load().LocalGenerationDiagnosticsEnabled);
                Assert.False(App.Settings.IncognitoMode);
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                App.ReplaceSettings(original);
                TestHelpers.ShutdownCurrentWpfDispatcher();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
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
    [Fact]
    public void UserTuningOptions_ExposeOnlyBasicAndAdvancedControls()
    {
        var vm = new SettingsViewModel(skillCatalogLoader: () => []);

        Assert.NotEmpty(vm.UserTuningOptions);
        Assert.All(vm.UserTuningOptions, option => Assert.True(option.UserEditable));
        Assert.DoesNotContain(vm.UserTuningOptions, option => option.Exposure == AgentTuningExposure.Internal);
        Assert.Contains(vm.UserTuningOptions, option => option.Id == "response_style");
        Assert.Contains(vm.UserTuningOptions, option => option.Id == "preferred_skills");
    }
}
