using Huaxiazi.Models;
using Huaxiazi.ViewModels;
using Huaxiazi.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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

    private sealed class RecordingClipboardService : IClipboardService
    {
        public string CopiedText { get; private set; } = string.Empty;
        public void CopyText(string text) => CopiedText = text;
        public string GetText() => string.Empty;
    }

    private sealed class CountingGenerationClient : ITextGenerationClient
    {
        private readonly string _response;

        public CountingGenerationClient(string response = "{\"kind\":\"final\",\"content\":\"不应生成\"}") => _response = response;

        public int Calls { get; private set; }
        public string LastUserInput { get; private set; } = string.Empty;
        public string LastSystemPrompt { get; private set; } = string.Empty;
        public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastSystemPrompt = systemPrompt;
            LastUserInput = userInput;
            return Task.FromResult(_response);
        }
    }

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
    public async Task OptimizeAsync_AsksForMissingExplicitDelayReasonBeforeCallingGenerationClient()
    {
        ResetSettings();
        App.Settings.ClarificationEnabled = true;
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "HuaxiaziClarification_" + Guid.NewGuid().ToString("N"));
        var client = new CountingGenerationClient();
        var clientFactoryCalls = 0;
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (_, _) =>
            {
                clientFactoryCalls++;
                return client;
            })
        {
            UserInput = "王总，项目版本预计于2026年9月20日交付，如果测试通过需要2天准备。",
            Purpose = "说明延期"
        };

        await vm.OptimizeAsync();

        Assert.True(vm.HasClarification);
        Assert.Contains(vm.ClarificationQuestions, question => question.Contains("具体原因", StringComparison.Ordinal));
        Assert.Equal(0, clientFactoryCalls);
        Assert.Equal(0, client.Calls);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task OptimizeAsync_AsksForProblemDetailsBeforeAnalysisWhenDescriptionIsMissing()
    {
        ResetSettings();
        App.Settings.ClarificationEnabled = true;
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "HuaxiaziClarification_" + Guid.NewGuid().ToString("N"));
        var client = new CountingGenerationClient();
        var clientFactoryCalls = 0;
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (_, _) =>
            {
                clientFactoryCalls++;
                return client;
            })
        {
            UserInput = "这件事我已经看过了，想和你同步一下进展。",
            Purpose = "问题分析"
        };

        await vm.OptimizeAsync();

        Assert.True(vm.HasClarification);
        Assert.Contains(vm.ClarificationQuestions, question => question.Contains("具体问题", StringComparison.Ordinal));
        Assert.Equal(0, clientFactoryCalls);
        Assert.Equal(0, client.Calls);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task OptimizeAsync_WhenClarificationIsDisabledAndFactsAreMissing_ExplainsGapWithoutCreatingClient()
    {
        ResetSettings();
        App.Settings.ClarificationEnabled = false;
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "HuaxiaziNoClarification_" + Guid.NewGuid().ToString("N"));
        var client = new CountingGenerationClient();
        var clientFactoryCalls = 0;
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (_, _) =>
            {
                clientFactoryCalls++;
                return client;
            })
        {
            UserInput = "这件事需要尽快处理。",
            Purpose = "问题分析"
        };

        await vm.OptimizeAsync();

        Assert.True(vm.HasError);
        Assert.Contains("具体问题或异常表现", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("未向模型发送请求", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.False(vm.HasClarification);
        Assert.Empty(vm.ClarificationQuestions);
        Assert.Equal(0, clientFactoryCalls);
        Assert.Equal(0, client.Calls);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task OptimizeAsync_WhenClarificationIsDisabledAndFactsArePresent_ContinuesGeneration()
    {
        ResetSettings();
        App.Settings.ClarificationEnabled = false;
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "HuaxiaziNoClarification_" + Guid.NewGuid().ToString("N"));
        var client = new CountingGenerationClient("{\"kind\":\"final\",\"content\":\"关于延期，原因是供应商设备故障。\"}");
        var clientFactoryCalls = 0;
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (_, _) =>
            {
                clientFactoryCalls++;
                return client;
            })
        {
            UserInput = "项目交付延期，因为供应商设备故障。",
            Purpose = "说明延期"
        };

        await vm.OptimizeAsync();

        Assert.False(vm.HasError);
        Assert.Equal(1, clientFactoryCalls);
        Assert.Equal(1, client.Calls);
        Assert.Contains("供应商设备故障", vm.OptimizedResult, StringComparison.Ordinal);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task OptimizeAsync_PromptMode_WhenClarificationIsDisabledAndTaskIsUnderspecified_DoesNotCreateClient()
    {
        ResetSettings();
        App.Settings.ClarificationEnabled = false;
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "HuaxiaziPromptNoClarification_" + Guid.NewGuid().ToString("N"));
        var client = new CountingGenerationClient();
        var clientFactoryCalls = 0;
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (_, _) =>
            {
                clientFactoryCalls++;
                return client;
            })
        {
            UserInput = "帮我优化一下"
        };
        vm.SelectModeCommand.Execute(ApplicationMode.PromptOptimize);

        await vm.OptimizeAsync();

        Assert.Equal(0, clientFactoryCalls);
        Assert.Equal(0, client.Calls);
        Assert.True(vm.HasError);
        Assert.Contains("具体任务", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("未向模型发送请求", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.False(vm.HasClarification);
        Assert.Empty(vm.ClarificationQuestions);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task OptimizeAsync_PromptMode_WhenClarificationIsEnabledAndTaskIsUnderspecified_AsksBeforeCreatingClient()
    {
        ResetSettings();
        App.Settings.ClarificationEnabled = true;
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "HuaxiaziPromptClarification_" + Guid.NewGuid().ToString("N"));
        var client = new CountingGenerationClient();
        var clientFactoryCalls = 0;
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (_, _) =>
            {
                clientFactoryCalls++;
                return client;
            })
        {
            UserInput = "帮我优化一下"
        };
        vm.SelectModeCommand.Execute(ApplicationMode.PromptOptimize);

        await vm.OptimizeAsync();

        Assert.True(vm.HasClarification);
        Assert.Contains(vm.ClarificationQuestions, question => question.Contains("具体任务", StringComparison.Ordinal));
        Assert.Equal(0, clientFactoryCalls);
        Assert.Equal(0, client.Calls);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task OptimizeAsync_PromptMode_WhenTaskHasEnoughInformation_ContinuesGeneration()
    {
        ResetSettings();
        App.Settings.ClarificationEnabled = false;
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "HuaxiaziPromptComplete_" + Guid.NewGuid().ToString("N"));
        var output = "请设计一个支持3个仓库的库存系统，包含入库、出库与库存预警，并输出实施步骤。";
        var client = new CountingGenerationClient(output);
        var clientFactoryCalls = 0;
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (_, _) =>
            {
                clientFactoryCalls++;
                return client;
            })
        {
            UserInput = "设计一个支持3个仓库的库存系统，包含入库、出库与库存预警，并输出实施步骤。"
        };
        vm.SelectModeCommand.Execute(ApplicationMode.PromptOptimize);

        await vm.OptimizeAsync();

        Assert.False(vm.HasError);
        Assert.False(vm.HasClarification);
        Assert.Equal(1, clientFactoryCalls);
        Assert.Equal(1, client.Calls);
        Assert.Contains("3个仓库", vm.OptimizedResult, StringComparison.Ordinal);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task RegenerateAsync_RecordsTheFinalEditedOutputAsRejectedPreferenceEvidence()
    {
        ResetSettings();
        App.Settings.ClarificationEnabled = false;
        App.Settings.PreferenceLearningEnabled = true;
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "HuaxiaziPreferenceRetry_" + Guid.NewGuid().ToString("N"));
        var output = "请设计一个支持3个仓库的库存系统，包含入库、出库与库存预警，并输出实施步骤。";
        var client = new CountingGenerationClient(output);
        try
        {
            var vm = new MainViewModel(
                new WorkspaceDraftService(root),
                new ArchiveService(root),
                (_, _) => client)
            {
                UserInput = output
            };
            vm.SelectModeCommand.Execute(ApplicationMode.PromptOptimize);

            await vm.OptimizeAsync();
            Assert.False(vm.HasError, vm.ErrorMessage);
            vm.OptimizedResult = "请设计库存系统。";

            await vm.RegenerateCommand.ExecuteAsync(null);

            var key = StructuredPreferenceService.GetTaskPreferenceKey(ApplicationMode.PromptOptimize, vm.SelectedCategory.GetDisplayName());
            var signals = App.Settings.ExpressionPreferenceProfile.InteractionSignals[key];
            Assert.Equal(1, signals.RetryCount);
            Assert.Equal(1, signals.RejectedShortenedOutputs);
            Assert.DoesNotContain("请设计库存系统。", System.Text.Json.JsonSerializer.Serialize(App.Settings.ExpressionPreferenceProfile), StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RegenerateAsync_ClosesOldScenarioFeedbackBeforeStartingTheNextScenario()
    {
        ResetSettings();
        App.Settings.ClarificationEnabled = false;
        App.Settings.PreferenceLearningEnabled = true;
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "HuaxiaziPreferenceRetryScenario_" + Guid.NewGuid().ToString("N"));
        const string output = "请设计一个支持3个仓库的库存系统，包含入库、出库与库存预警，并输出实施步骤。";
        var client = new CountingGenerationClient(output);
        var clipboard = new RecordingClipboardService();
        try
        {
            var vm = new MainViewModel(
                new WorkspaceDraftService(root),
                new ArchiveService(root),
                (_, _, _, _) => client,
                clipboard)
            {
                UserInput = output
            };
            vm.SelectModeCommand.Execute(ApplicationMode.PromptOptimize);
            vm.SelectedCategory = PromptCategory.General;

            await vm.OptimizeAsync();
            Assert.False(vm.HasError, vm.ErrorMessage);
            vm.OptimizedResult = "请设计库存系统。";
            vm.SelectedCategory = PromptCategory.Coding;

            await vm.RegenerateCommand.ExecuteAsync(null);
            Assert.False(vm.HasError, vm.ErrorMessage);
            Assert.Equal(2, client.Calls);

            var generalKey = StructuredPreferenceService.GetTaskPreferenceKey(ApplicationMode.PromptOptimize, PromptCategory.General.GetDisplayName());
            var codingKey = StructuredPreferenceService.GetTaskPreferenceKey(ApplicationMode.PromptOptimize, PromptCategory.Coding.GetDisplayName());
            var profile = App.Settings.ExpressionPreferenceProfile;
            var rejectedSignals = profile.InteractionSignals[generalKey];
            Assert.Equal(1, rejectedSignals.RetryCount);
            Assert.Equal(1, rejectedSignals.RejectedShortenedOutputs);
            Assert.False(profile.InteractionSignals.ContainsKey(codingKey));

            vm.OptimizedResult = new string('扩', output.Length + 40);
            vm.CopyResultCommand.Execute(null);

            var acceptedSignals = profile.InteractionSignals[codingKey];
            Assert.Equal(1, acceptedSignals.AcceptedCount);
            Assert.Equal(1, acceptedSignals.AcceptedExpandedOutputs);
            Assert.Equal(1, rejectedSignals.RetryCount);
            Assert.Equal(1, profile.RetryCount);
            Assert.Equal(1, profile.AcceptedCount);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CopyResult_RecordsTheFinalEditedOutputAsAcceptedPreferenceEvidence()
    {
        ResetSettings();
        App.Settings.ClarificationEnabled = false;
        App.Settings.PreferenceLearningEnabled = true;
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "HuaxiaziPreferenceCopy_" + Guid.NewGuid().ToString("N"));
        var output = "请设计一个支持3个仓库的库存系统，包含入库、出库与库存预警，并输出实施步骤。";
        var client = new CountingGenerationClient(output);
        var clipboard = new RecordingClipboardService();
        try
        {
            var vm = new MainViewModel(
                new WorkspaceDraftService(root),
                new ArchiveService(root),
                (_, _, _, _) => client,
                clipboard)
            {
                UserInput = output
            };
            vm.SelectModeCommand.Execute(ApplicationMode.PromptOptimize);

            await vm.OptimizeAsync();
            Assert.False(vm.HasError, vm.ErrorMessage);
            vm.OptimizedResult = "请设计库存系统。";

            vm.CopyResultCommand.Execute(null);

            var key = StructuredPreferenceService.GetTaskPreferenceKey(ApplicationMode.PromptOptimize, vm.SelectedCategory.GetDisplayName());
            var signals = App.Settings.ExpressionPreferenceProfile.InteractionSignals[key];
            Assert.Equal("请设计库存系统。", clipboard.CopiedText);
            Assert.Equal(1, signals.AcceptedCount);
            Assert.Equal(1, signals.AcceptedShortenedOutputs);
            Assert.DoesNotContain("请设计库存系统。", System.Text.Json.JsonSerializer.Serialize(App.Settings.ExpressionPreferenceProfile), StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task UndoWorkspace_RecordsTheFinalEditedOutputAsRejectedPreferenceEvidence()
    {
        ResetSettings();
        App.Settings.ClarificationEnabled = false;
        App.Settings.PreferenceLearningEnabled = true;
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "HuaxiaziPreferenceUndo_" + Guid.NewGuid().ToString("N"));
        var output = "请设计一个支持3个仓库的库存系统，包含入库、出库与库存预警，并输出实施步骤。";
        var client = new CountingGenerationClient(output);
        try
        {
            var vm = new MainViewModel(
                new WorkspaceDraftService(root),
                new ArchiveService(root),
                (_, _) => client)
            {
                UserInput = output
            };
            vm.SelectModeCommand.Execute(ApplicationMode.PromptOptimize);

            await vm.OptimizeAsync();
            Assert.False(vm.HasError, vm.ErrorMessage);
            vm.OptimizedResult = "请设计库存系统。";

            vm.UndoWorkspaceCommand.Execute(null);

            var key = StructuredPreferenceService.GetTaskPreferenceKey(ApplicationMode.PromptOptimize, vm.SelectedCategory.GetDisplayName());
            var signals = App.Settings.ExpressionPreferenceProfile.InteractionSignals[key];
            Assert.Equal(1, signals.UndoCount);
            Assert.Equal(1, signals.RejectedShortenedOutputs);
            Assert.DoesNotContain("请设计库存系统。", System.Text.Json.JsonSerializer.Serialize(App.Settings.ExpressionPreferenceProfile), StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    public async Task CopyResult_LearnsOnlyWhenEnabledAndNotIncognito(bool incognito, bool learningEnabled, bool expectedToLearn)
    {
        ResetSettings();
        App.Settings.ClarificationEnabled = false;
        App.Settings.IncognitoMode = incognito;
        App.Settings.PreferenceLearningEnabled = learningEnabled;
        App.Settings.ExpressionPreferenceProfile.AcceptedCount = 3;
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "HuaxiaziPreferencePrivacy_" + Guid.NewGuid().ToString("N"));
        var output = "请设计一个支持3个仓库的库存系统，包含入库、出库与库存预警，并输出实施步骤。";
        var client = new CountingGenerationClient(output);
        var clipboard = new RecordingClipboardService();
        try
        {
            var vm = new MainViewModel(
                new WorkspaceDraftService(root),
                new ArchiveService(root),
                (_, _, _, _) => client,
                clipboard)
            {
                UserInput = output
            };
            vm.SelectModeCommand.Execute(ApplicationMode.PromptOptimize);

            await vm.OptimizeAsync();
            Assert.False(vm.HasError, vm.ErrorMessage);
            vm.OptimizedResult = "请设计库存系统。";
            vm.CopyResultCommand.Execute(null);

            var profile = App.Settings.ExpressionPreferenceProfile;
            Assert.Equal("请设计库存系统。", clipboard.CopiedText);
            Assert.Equal(expectedToLearn ? 4 : 3, profile.AcceptedCount);
            Assert.Equal(expectedToLearn, profile.InteractionSignals.Count == 1);
            Assert.Equal(incognito, !App.Settings.History.Contains(output, StringComparer.Ordinal));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CopyResult_DoesNotResumeAFeedbackSessionAfterIncognitoTransition(bool generatedInIncognito)
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "HuaxiaziPreferenceSessionPrivacy_" + Guid.NewGuid().ToString("N"));
        const string output = "请设计一个支持3个仓库的库存系统，包含入库、出库与库存预警，并输出实施步骤。";
        var client = new CountingGenerationClient(output);
        var clipboard = new RecordingClipboardService();
        App.ReplaceSettings(new AppSettings
        {
            DataDirectory = root,
            IncognitoMode = generatedInIncognito,
            PreferenceLearningEnabled = true,
            ClarificationEnabled = false
        });
        try
        {
            var vm = new MainViewModel(
                new WorkspaceDraftService(root),
                new ArchiveService(root),
                (_, _, _, _) => client,
                clipboard)
            {
                UserInput = output
            };
            vm.SelectModeCommand.Execute(ApplicationMode.PromptOptimize);
            await vm.OptimizeAsync();
            Assert.False(vm.HasError, vm.ErrorMessage);

            if (!generatedInIncognito)
            {
                App.Settings.IncognitoMode = true;
                vm.ReloadPreferences();
            }
            App.Settings.IncognitoMode = false;
            vm.ReloadPreferences();

            vm.OptimizedResult = "请设计库存系统。";
            vm.CopyResultCommand.Execute(null);

            Assert.Equal("请设计库存系统。", clipboard.CopiedText);
            Assert.Equal(0, App.Settings.ExpressionPreferenceProfile.AcceptedCount);
            Assert.Empty(App.Settings.ExpressionPreferenceProfile.InteractionSignals);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CopyResult_DoesNotResumeAFeedbackSessionAfterLearningToggle(bool learningEnabledAtGeneration)
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "HuaxiaziPreferenceLearningTransition_" + Guid.NewGuid().ToString("N"));
        const string output = "请设计一个支持3个仓库的库存系统，包含入库、出库与库存预警，并输出实施步骤。";
        var client = new CountingGenerationClient(output);
        var clipboard = new RecordingClipboardService();
        App.ReplaceSettings(new AppSettings
        {
            DataDirectory = root,
            PreferenceLearningEnabled = learningEnabledAtGeneration,
            IncognitoMode = false,
            ClarificationEnabled = false
        });
        try
        {
            var vm = new MainViewModel(
                new WorkspaceDraftService(root),
                new ArchiveService(root),
                (_, _, _, _) => client,
                clipboard)
            {
                UserInput = output
            };
            vm.SelectModeCommand.Execute(ApplicationMode.PromptOptimize);
            await vm.OptimizeAsync();
            Assert.False(vm.HasError, vm.ErrorMessage);

            if (learningEnabledAtGeneration)
            {
                App.Settings.PreferenceLearningEnabled = false;
                vm.ReloadPreferences();
            }
            App.Settings.PreferenceLearningEnabled = true;
            vm.ReloadPreferences();

            vm.OptimizedResult = "请设计库存系统。";
            vm.CopyResultCommand.Execute(null);

            Assert.Equal("请设计库存系统。", clipboard.CopiedText);
            Assert.Equal(0, App.Settings.ExpressionPreferenceProfile.AcceptedCount);
            Assert.Empty(App.Settings.ExpressionPreferenceProfile.InteractionSignals);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task RetryOrUndo_DoesNotRecordAFeedbackSessionAfterPrivacyTransition(bool transitionToIncognito, bool retry)
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "HuaxiaziPreferenceOutcomePrivacy_" + Guid.NewGuid().ToString("N"));
        const string output = "请设计一个支持3个仓库的库存系统，包含入库、出库与库存预警，并输出实施步骤。";
        var client = new CountingGenerationClient(output);
        App.ReplaceSettings(new AppSettings
        {
            DataDirectory = root,
            PreferenceLearningEnabled = true,
            IncognitoMode = false,
            ClarificationEnabled = false
        });
        try
        {
            var vm = new MainViewModel(
                new WorkspaceDraftService(root),
                new ArchiveService(root),
                (_, _, _, _) => client)
            {
                UserInput = output
            };
            vm.SelectModeCommand.Execute(ApplicationMode.PromptOptimize);
            await vm.OptimizeAsync();
            Assert.False(vm.HasError, vm.ErrorMessage);

            if (transitionToIncognito) App.Settings.IncognitoMode = true;
            else App.Settings.PreferenceLearningEnabled = false;
            vm.ReloadPreferences();
            if (transitionToIncognito) App.Settings.IncognitoMode = false;
            else App.Settings.PreferenceLearningEnabled = true;
            vm.ReloadPreferences();

            vm.OptimizedResult = "请设计库存系统。";
            if (retry) await vm.RegenerateCommand.ExecuteAsync(null);
            else vm.UndoWorkspaceCommand.Execute(null);

            var profile = App.Settings.ExpressionPreferenceProfile;
            Assert.Equal(0, profile.AcceptedCount);
            Assert.Equal(0, profile.RetryCount);
            Assert.Equal(0, profile.UndoCount);
            Assert.Empty(profile.InteractionSignals);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("cloud", false, false)]
    [InlineData("cloud", true, true)]
    [InlineData("local", false, true)]
    public async Task OptimizeAsync_IncludesConfirmedPreferenceOnlyForLocalOrConsentedCloud(string providerKind, bool shareWithCloud, bool expectedInPrompt)
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "HuaxiaziPreferenceCloud_" + Guid.NewGuid().ToString("N"));
        const string privatePreference = "PRIVATE-PREFERENCE-MARKER-7291";
        const string privateInteractionSignal = "PRIVATE-INTERACTION-SIGNAL-5284";
        const string input = "设计一个支持3个仓库的库存系统，包含入库、出库与库存预警，并输出实施步骤。";
        var client = new CountingGenerationClient(input);
        var provider = providerKind == "local"
            ? new ProviderProfile
            {
                Id = "selected",
                Type = ProviderType.Local,
                Platform = ProviderPlatform.Ollama,
                Protocol = ProviderProtocol.OpenAICompatible,
                ApiBase = "http://localhost:11434/v1",
                Model = "qwen3:4b"
            }
            : new ProviderProfile
            {
                Id = "selected",
                Type = ProviderType.Cloud,
                Platform = ProviderPlatform.OpenAI,
                ApiBase = "https://api.openai.com/v1",
                Model = "gpt-4o-mini"
            };
        App.ReplaceSettings(new AppSettings
        {
            DataDirectory = root,
            ClarificationEnabled = false,
            DefaultCategory = "编程开发",
            ShareConfirmedPreferencesWithCloud = shareWithCloud,
            ProviderProfiles = [provider],
            ActiveProviderProfileId = provider.Id,
            ExpressionPreferenceProfile = new ExpressionPreferenceProfile
            {
                RemovedCannedExpressions = new Dictionary<string, int> { [privateInteractionSignal] = 4 },
                TaskPreferences = new Dictionary<string, ExpressionPreferenceSet>
                {
                    ["prompt-optimize|编程开发"] = new()
                    {
                        UserConfirmed = true,
                        ForbiddenExpressions = [privatePreference]
                    }
                }
            }
        });
        try
        {
            var vm = new MainViewModel(
                new WorkspaceDraftService(root),
                new ArchiveService(root),
                (_, _) => client)
            {
                UserInput = input
            };
            vm.SelectModeCommand.Execute(ApplicationMode.PromptOptimize);

            await vm.OptimizeAsync();

            Assert.False(vm.HasError, vm.ErrorMessage);
            Assert.Equal(expectedInPrompt, client.LastSystemPrompt.Contains(privatePreference, StringComparison.Ordinal));
            Assert.DoesNotContain(privateInteractionSignal, client.LastSystemPrompt, StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SubmitClarificationAsync_IncludesUserAnswerInGenerationAndClearsClarification()
    {
        ResetSettings();
        App.Settings.ClarificationEnabled = true;
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", "HuaxiaziClarification_" + Guid.NewGuid().ToString("N"));
        var client = new CountingGenerationClient("{\"kind\":\"final\",\"content\":\"关于延期，原因是供应商设备故障。\"}");
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (_, _) => client)
        {
            UserInput = "请帮我说明延期。",
            Purpose = "说明延期"
        };

        await vm.OptimizeAsync();
        Assert.True(vm.HasClarification);
        Assert.Equal(0, client.Calls);

        vm.ClarificationAnswer = "延期原因是供应商设备故障。";
        await vm.SubmitClarificationCommand.ExecuteAsync(null);

        Assert.Equal(1, client.Calls);
        Assert.Contains("供应商设备故障", client.LastUserInput, StringComparison.Ordinal);
        Assert.False(vm.HasClarification);
        Assert.Empty(vm.ClarificationQuestions);
        Assert.Empty(vm.ClarificationAnswer);
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
    public void CompanionState_UsesDistinctStatesForStartProcessingQuestionAndCompletion()
    {
        var vm = new MainViewModel();

        vm.SetCompanionWorkPhase(CompanionWorkPhase.Starting);
        vm.IsBusy = true;
        Assert.Equal(CompanionVisualState.Working, vm.CompanionState);

        vm.SetCompanionWorkPhase(CompanionWorkPhase.Processing);
        Assert.Equal(CompanionVisualState.Thinking, vm.CompanionState);

        vm.IsBusy = false;
        vm.HasClarification = true;
        Assert.Equal(CompanionVisualState.Curious, vm.CompanionState);

        vm.HasClarification = false;
        vm.ArchiveStatus = "未归档";
        vm.SetCompanionCompletion(true);
        Assert.Equal(CompanionVisualState.Happy, vm.CompanionState);
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
