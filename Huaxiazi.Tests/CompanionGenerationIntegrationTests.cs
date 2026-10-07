using System.Net;
using System.Net.Http;
using System.Text;
using System.IO;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Huaxiazi.ViewModels;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class CompanionGenerationIntegrationTests : IDisposable
{
    private readonly AppSettings _originalSettings = App.Settings.Clone();

    private static string CreateTestRoot(string name)
    {
        var root = Path.Combine(Environment.CurrentDirectory, "out", "test-artifacts", name + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public void Dispose() => App.ReplaceSettings(_originalSettings);

    [Fact]
    public async Task MainViewModel_CorrelatesEditedOutputWithRetryWithoutStoringText()
    {
        using var configScope = TestHelpers.UseIsolatedConfigDirectory();
        var root = CreateTestRoot("PreferenceFeedbackIntegration");
        Directory.CreateDirectory(root);
        var profile = new ProviderProfile
        {
            Id = "test-cloud",
            Name = "Fake Provider",
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.example.com/v1",
            Model = "fake-model"
        };
        App.ReplaceSettings(new AppSettings
        {
            DataDirectory = root,
            ProviderProfiles = [profile],
            ActiveProviderProfileId = profile.Id,
            PreferenceLearningEnabled = true,
            IncognitoMode = false,
            HistoryEnabled = false,
            AutoArchive = false
        });
        var responses = new Queue<string>([
            "{\"kind\":\"final\",\"content\":\"首先" + new string('原', 100) + "\"}",
            "{\"kind\":\"final\",\"content\":\"第二次生成结果\"}"
        ]);
        var createdTasks = new List<ApplicationMode>();
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (_, _, task) =>
            {
                createdTasks.Add(task);
                return new CapturingClient(responses.Dequeue());
            })
        {
            UserInput = "需要润色的原文"
        };

        try
        {
            await vm.OptimizeCommand.ExecuteAsync(null);
            Assert.False(vm.HasError, vm.ErrorMessage);
            vm.OptimizedResult = new string('改', 50);
            vm.SaveEditedResultCommand.Execute(null);
            Assert.False(vm.HasError, vm.ErrorMessage);

            await vm.RegenerateCommand.ExecuteAsync(null);

            Assert.NotEmpty(createdTasks);
            Assert.All(createdTasks, task => Assert.Equal(ApplicationMode.Polish, task));

            var signal = Assert.Single(App.Settings.ExpressionPreferenceProfile.InteractionSignals.Values,
                value => value.RejectedShortenedOutputs == 1);
            Assert.Equal(1, signal.EditCount);
            Assert.Equal(1, signal.RejectedOutputStyles["自然"]);
            Assert.Equal(1, signal.RejectedRemovedCannedExpressions["首先"]);
            var serialized = System.Text.Json.JsonSerializer.Serialize(App.Settings.ExpressionPreferenceProfile);
            Assert.DoesNotContain(new string('原', 100), serialized, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('改', 50), serialized, StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task MainViewModel_PromptOptimizeUsesTheConfiguredOutputStyle()
    {
        var root = CreateTestRoot("PromptOutputStyleIntegration");
        var profile = new ProviderProfile
        {
            Id = "test-cloud",
            Name = "Fake Provider",
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.example.com/v1",
            Model = "fake-model"
        };
        App.ReplaceSettings(new AppSettings
        {
            DataDirectory = root,
            ProviderProfiles = [profile],
            ActiveProviderProfileId = profile.Id,
            OutputStyle = "正式",
            IncognitoMode = true,
            HistoryEnabled = false,
            AutoArchive = false
        });
        var client = new CapturingClient("{\"answer\":\"正式书面提示词\"}");
        var createdTasks = new List<ApplicationMode>();
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (_, _, task) =>
            {
                createdTasks.Add(task);
                return client;
            })
        {
            CurrentMode = ApplicationMode.PromptOptimize,
            UserInput = "写一个客户延期通知"
        };

        try
        {
            await vm.OptimizeCommand.ExecuteAsync(null);

            Assert.False(vm.HasError, vm.ErrorMessage);
            Assert.Contains("用户选择的输出风格：正式书面表达", client.SystemPrompts.Single());
            Assert.Equal([ApplicationMode.PromptOptimize], createdTasks);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Theory]
    [InlineData(ApplicationMode.Polish)]
    [InlineData(ApplicationMode.PromptOptimize)]
    public async Task MainViewModel_CorrelatesProviderAttemptWithFinalWorkflowQualityGate(ApplicationMode task)
    {
        using var configScope = TestHelpers.UseIsolatedConfigDirectory();
        var root = CreateTestRoot("WorkflowQualityDiagnostics_" + task);
        var profile = new ProviderProfile
        {
            Id = "diagnostics-provider",
            Name = "诊断测试模型",
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.example.com/v1",
            Model = "diagnostics-model"
        };
        App.ReplaceSettings(new AppSettings
        {
            DataDirectory = root,
            LocalGenerationDiagnosticsEnabled = true,
            ProviderProfiles = [profile],
            ActiveProviderProfileId = profile.Id,
            IncognitoMode = false,
            HistoryEnabled = false,
            AutoArchive = false,
            ClarificationEnabled = true
        });
        var input = task == ApplicationMode.Polish
            ? "我周五前完成，并同步进度。"
            : "设计一个支持3个仓库的库存系统，要有验收标准";
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (selectedProfile, _, selectedTask, scope) =>
            {
                Assert.Equal(task, selectedTask);
                Assert.NotNull(scope);
                scope!.RecordRequest(selectedProfile, new ProviderRequestTelemetry("req-quality-1", 125, 30, 12, 200, "success"));
                return new StructuredWorkflowDiagnosticsClient(task);
            })
        {
            CurrentMode = task,
            UserInput = input
        };

        try
        {
            await vm.OptimizeCommand.ExecuteAsync(null);

            Assert.False(vm.HasError, vm.ErrorMessage);
            var service = new GenerationDiagnosticsService(Path.Combine(root, "diagnostics"));
            var request = Assert.Single(service.ReadRecent());
            var workflow = Assert.Single(service.ReadWorkflowRecent());
            Assert.Equal(request.GenerationId, workflow.GenerationId);
            Assert.Equal(task.ToString(), workflow.Task);
            Assert.Equal("final", workflow.Outcome);
            Assert.True(workflow.OutputContractValid);
            Assert.True(workflow.StructuredOutputValid);
            Assert.True(workflow.QualityGatePassed);
            Assert.False(workflow.WasRepaired);
            Assert.Equal(0, workflow.ValidationIssueCount);
            Assert.Equal(1, workflow.RequestCount);
            Assert.DoesNotContain(input, File.ReadAllText(service.WorkflowFilePath), StringComparison.Ordinal);
            Assert.DoesNotContain(vm.OptimizedResult, File.ReadAllText(service.WorkflowFilePath), StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task MainViewModel_PromptOptimizePrefersTheExplicitTaskScenarioStyle()
    {
        var root = CreateTestRoot("PromptScopedOutputStyleIntegration");
        var profile = new ProviderProfile
        {
            Id = "test-cloud",
            Name = "Fake Provider",
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.example.com/v1",
            Model = "fake-model"
        };
        App.ReplaceSettings(new AppSettings
        {
            DataDirectory = root,
            ProviderProfiles = [profile],
            ActiveProviderProfileId = profile.Id,
            OutputStyle = "正式",
            OutputStyleOverrides = new Dictionary<string, string>
            {
                ["PromptOptimize|编程开发"] = "克制"
            },
            IncognitoMode = true,
            HistoryEnabled = false,
            AutoArchive = false
        });
        var client = new CapturingClient("{\"answer\":\"重构后的提示词\"}");
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (_, _) => client)
        {
            CurrentMode = ApplicationMode.PromptOptimize,
            SelectedCategory = PromptCategory.Coding,
            UserInput = "重构这个模块"
        };

        try
        {
            await vm.OptimizeCommand.ExecuteAsync(null);

            Assert.False(vm.HasError, vm.ErrorMessage);
            Assert.Contains("用户选择的输出风格：克制表达，不过度热情或夸张", client.SystemPrompts.Single());
            Assert.Equal("最近成稿风格：克制", vm.LastUsedOutputStyleLabel);
            Assert.DoesNotContain("用户选择的输出风格：正式书面表达", client.SystemPrompts.Single());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task MainViewModel_PolishUsesTheExplicitScenarioStyleOverride()
    {
        var root = CreateTestRoot("PolishScopedOutputStyleIntegration");
        var profile = new ProviderProfile
        {
            Id = "test-cloud",
            Name = "Fake Provider",
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.example.com/v1",
            Model = "fake-model"
        };
        App.ReplaceSettings(new AppSettings
        {
            DataDirectory = root,
            ProviderProfiles = [profile],
            ActiveProviderProfileId = profile.Id,
            OutputStyle = "正式",
            OutputStyleOverrides = new Dictionary<string, string>
            {
                ["Polish|职场沟通"] = "专业"
            },
            IncognitoMode = true,
            HistoryEnabled = false,
            AutoArchive = false
        });
        var client = new CapturingClient("{\"kind\":\"final\",\"content\":\"请确认项目交付时间。\"}");
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (_, _) => client)
        {
            CurrentMode = ApplicationMode.Polish,
            Scenario = "职场沟通",
            UserInput = "项目什么时候交付？"
        };

        try
        {
            await vm.OptimizeCommand.ExecuteAsync(null);

            Assert.False(vm.HasError, vm.ErrorMessage);
            Assert.Contains("输出风格：专业", client.SystemPrompts.Single());
            Assert.Equal("最近成稿风格：专业", vm.LastUsedOutputStyleLabel);
            Assert.DoesNotContain("正式书面表达", client.SystemPrompts.Single());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task PromptOptimizationArchive_StoresActualOutputStyleAndKeepsDepthInContext()
    {
        var root = CreateTestRoot("PromptStyleArchive");
        Directory.CreateDirectory(root);
        var profile = new ProviderProfile
        {
            Id = "test-cloud",
            Name = "Fake Provider",
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.example.com/v1",
            Model = "fake-model"
        };
        App.ReplaceSettings(new AppSettings
        {
            DataDirectory = root,
            ProviderProfiles = [profile],
            ActiveProviderProfileId = profile.Id,
            OutputStyle = "正式",
            OutputStyleOverrides = new Dictionary<string, string>
            {
                ["PromptOptimize|编程开发"] = "克制"
            },
            HistoryEnabled = true,
            AutoArchive = true,
            IncognitoMode = false
        });
        var archive = new ArchiveService(root);
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            archive,
            (_, _) => new CapturingClient("{\"answer\":\"先分析现状，再给出可执行的重构步骤。\"}"))
        {
            CurrentMode = ApplicationMode.PromptOptimize,
            SelectedCategory = PromptCategory.Coding,
            SelectedDepth = PromptDepth.Detailed,
            UserInput = "重构这个模块"
        };

        try
        {
            await vm.OptimizeCommand.ExecuteAsync(null);

            Assert.False(vm.HasError, vm.ErrorMessage);
            var revision = Assert.Single(archive.Search(null));
            Assert.Equal("克制", revision.Style);
            using var context = System.Text.Json.JsonDocument.Parse(revision.ContextJson);
            Assert.Equal((int)PromptDepth.Detailed, context.RootElement.GetProperty("Depth").GetInt32());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task MainViewModel_LocalOnlyWithoutLocalProfile_DoesNotCreateAnyGenerationClient()
    {
        var cloud = new ProviderProfile
        {
            Id = "cloud",
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.example.com/v1",
            Model = "cloud-model"
        };
        App.ReplaceSettings(new AppSettings
        {
            ProviderProfiles = [cloud],
            ActiveProviderProfileId = cloud.Id,
            ProviderRoutingMode = ProviderRoutingMode.LocalOnly,
            IncognitoMode = true
        });
        var root = CreateTestRoot("LocalOnlyNoCloudVm");
        var factoryCalls = 0;
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (_, _) =>
            {
                factoryCalls++;
                return new ThrowingClient(new InvalidOperationException("云端客户端不应创建"));
            })
        {
            UserInput = "请润色这句话"
        };

        try
        {
            await vm.OptimizeCommand.ExecuteAsync(null);

            Assert.Equal(0, factoryCalls);
            Assert.True(vm.HasError);
            Assert.Contains("不会改用云端", vm.OperationalNotice);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(GenerationFailureKind.Timeout)]
    [InlineData(GenerationFailureKind.ContextLimitExceeded)]
    public async Task MainViewModel_LocalOnlyLocalFailureNeverCreatesConfiguredCloudFallback(GenerationFailureKind failureKind)
    {
        var local = new ProviderProfile
        {
            Id = "local-only-primary",
            Name = "仅本地模型",
            Type = ProviderType.Local,
            Platform = ProviderPlatform.Ollama,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "http://localhost:11434/v1",
            Model = "local-model"
        };
        var cloud = new ProviderProfile
        {
            Id = "configured-cloud-backup",
            Name = "已配置云端备用",
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.example.com/v1",
            Model = "cloud-model"
        };
        App.ReplaceSettings(new AppSettings
        {
            ProviderProfiles = [local, cloud],
            ActiveProviderProfileId = cloud.Id,
            PolishProviderProfileId = local.Id,
            FallbackProviderProfileId = cloud.Id,
            ProviderRoutingMode = ProviderRoutingMode.LocalOnly,
            IncognitoMode = true
        });
        var root = CreateTestRoot("LocalOnlyFailureBlocksCloudFallback");
        var createdProfiles = new List<string>();
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (profile, _) =>
            {
                createdProfiles.Add(profile.Id);
                return new ThrowingClient(new GenerationFailureException(
                    failureKind,
                    failureKind == GenerationFailureKind.ContextLimitExceeded ? "本地模型上下文窗口不足" : "本地模型超时",
                    isTransient: failureKind == GenerationFailureKind.Timeout));
            })
        {
            UserInput = "请润色这句话"
        };

        try
        {
            await vm.OptimizeCommand.ExecuteAsync(null);

            Assert.Equal(new[] { local.Id }, createdProfiles);
            Assert.True(vm.HasError);
            Assert.Contains(local.Name, vm.LastUsedProviderLabel);
            Assert.DoesNotContain(cloud.Name, vm.LastUsedProviderLabel);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MainViewModel_AutomaticRoutingUsesPlannedTierProfileAndReportsActualModel()
    {
        ProviderProfile Profile(string id, string model) => new()
        {
            Id = id,
            Name = id,
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.example.com/v1",
            Model = model
        };
        var fast = Profile("fast", "fast-model");
        var balanced = Profile("balanced", "balanced-model");
        var reasoning = Profile("reasoning", "reasoning-model");
        App.ReplaceSettings(new AppSettings
        {
            ProviderProfiles = [fast, balanced, reasoning],
            ActiveProviderProfileId = fast.Id,
            ProviderRoutingMode = ProviderRoutingMode.Automatic,
            PolishFastProviderProfileId = fast.Id,
            PolishBalancedProviderProfileId = balanced.Id,
            PolishReasoningProviderProfileId = reasoning.Id,
            PromptOptimizeFastProviderProfileId = fast.Id,
            PromptOptimizeBalancedProviderProfileId = balanced.Id,
            PromptOptimizeReasoningProviderProfileId = reasoning.Id,
            IncognitoMode = true
        });
        var root = CreateTestRoot("AutomaticTierProviderRouting");
        var selectedProfileIds = new List<string>();
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (profile, _) =>
            {
                selectedProfileIds.Add(profile.Id);
                return new CapturingClient("{\"answer\":\"按权限、审计与回滚要求构建库存系统。\"}");
            })
        {
            CurrentMode = ApplicationMode.PromptOptimize,
            SelectedCategory = PromptCategory.Coding,
            SelectedDepth = PromptDepth.Detailed,
            UserInput = "设计一个需要权限、审计和回滚策略的企业级库存系统"
        };

        try
        {
            await vm.OptimizeCommand.ExecuteAsync(null);

            Assert.Equal(new[] { reasoning.Id }, selectedProfileIds);
            Assert.False(vm.HasError, vm.ErrorMessage);
            Assert.Contains("reasoning-model", vm.LastUsedProviderLabel);
            Assert.Contains("Reasoning", vm.LastUsedProviderLabel);
            Assert.Contains("复杂推理", vm.LastUsedProviderLabel);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MainViewModel_AutomaticRoutingAsksForClarificationBeforeRequiringTierBinding()
    {
        var profile = new ProviderProfile
        {
            Id = "active",
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.example.com/v1",
            Model = "active-model"
        };
        App.ReplaceSettings(new AppSettings
        {
            ProviderProfiles = [profile],
            ActiveProviderProfileId = profile.Id,
            ProviderRoutingMode = ProviderRoutingMode.Automatic,
            IncognitoMode = true
        });
        var root = CreateTestRoot("AutomaticRoutingClarificationFirst");
        var factoryCalls = 0;
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (_, _) =>
            {
                factoryCalls++;
                return new CapturingClient("{\"answer\":\"unused\"}");
            })
        {
            CurrentMode = ApplicationMode.PromptOptimize,
            UserInput = "帮我优化一下"
        };

        try
        {
            await vm.OptimizeCommand.ExecuteAsync(null);

            Assert.True(vm.HasClarification);
            Assert.NotEmpty(vm.ClarificationQuestions);
            Assert.Equal(0, factoryCalls);
            Assert.False(vm.HasError, vm.ErrorMessage);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MainViewModel_PreferLocalUsesOnlyExplicitBackupAfterProviderFailure()
    {
        var local = new ProviderProfile
        {
            Id = "local",
            Name = "本地模型",
            Type = ProviderType.Local,
            Platform = ProviderPlatform.Ollama,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "http://localhost:11434/v1",
            Model = "local-model"
        };
        var cloud = new ProviderProfile
        {
            Id = "cloud-backup",
            Name = "备用云模型",
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.example.com/v1",
            Model = "backup-model"
        };
        App.ReplaceSettings(new AppSettings
        {
            ProviderProfiles = [cloud, local],
            ActiveProviderProfileId = cloud.Id,
            ProviderRoutingMode = ProviderRoutingMode.PreferLocal,
            PolishProviderProfileId = local.Id,
            FallbackProviderProfileId = cloud.Id,
            IncognitoMode = true
        });
        var root = CreateTestRoot("PreferLocalFallbackVm");
        var selectedProfileIds = new List<string>();
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (profile, _) =>
            {
                selectedProfileIds.Add(profile.Id);
                return profile.Id == local.Id
                    ? new ThrowingClient(new GenerationFailureException(GenerationFailureKind.Timeout, "本地模型超时", isTransient: true))
                    : new CapturingClient("{\"kind\":\"final\",\"content\":\"备用模型结果\"}");
            })
        {
            UserInput = "请润色这句话"
        };

        try
        {
            await vm.OptimizeCommand.ExecuteAsync(null);

            Assert.Equal(new[] { local.Id, cloud.Id }, selectedProfileIds);
            Assert.False(vm.HasError);
            Assert.Equal("备用模型结果", vm.OptimizedResult);
            Assert.Contains("备用云模型 · backup-model", vm.LastUsedProviderLabel);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MainViewModel_ManualModeUsesExplicitBackupAfterPrimaryFailure()
    {
        var cloud = new ProviderProfile
        {
            Id = "manual-primary",
            Name = "手动主模型",
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.example.com/v1",
            Model = "primary-model"
        };
        var local = new ProviderProfile
        {
            Id = "manual-backup",
            Name = "明确配置的本地备用",
            Type = ProviderType.Local,
            Platform = ProviderPlatform.Ollama,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "http://localhost:11434/v1",
            Model = "backup-model"
        };
        App.ReplaceSettings(new AppSettings
        {
            ProviderProfiles = [cloud, local],
            ActiveProviderProfileId = cloud.Id,
            ProviderRoutingMode = ProviderRoutingMode.Manual,
            FallbackProviderProfileId = local.Id,
            IncognitoMode = true
        });
        var root = CreateTestRoot("ManualFallbackVm");
        var selectedProfileIds = new List<string>();
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (profile, _) =>
            {
                selectedProfileIds.Add(profile.Id);
                return profile.Id == cloud.Id
                    ? new ThrowingClient(new GenerationFailureException(GenerationFailureKind.Timeout, "主模型超时", isTransient: true))
                    : new CapturingClient("{\"kind\":\"final\",\"content\":\"备用模型结果\"}");
            })
        {
            UserInput = "请润色这句话"
        };

        try
        {
            await vm.OptimizeCommand.ExecuteAsync(null);

            Assert.Equal(new[] { cloud.Id, local.Id }, selectedProfileIds);
            Assert.False(vm.HasError);
            Assert.Equal("备用模型结果", vm.OptimizedResult);
            Assert.Contains("明确配置的本地备用 · backup-model", vm.LastUsedProviderLabel);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MainViewModel_RecordsEditAgainstTheGeneratedPolishScenario()
    {
        using var configScope = TestHelpers.UseIsolatedConfigDirectory();
        var profile = new ProviderProfile
        {
            Id = "manual-cloud",
            Name = "测试云模型",
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            ApiBase = "https://api.example.test/v1",
            Model = "test-model"
        };
        App.ReplaceSettings(new AppSettings
        {
            ProviderProfiles = [profile],
            ActiveProviderProfileId = profile.Id,
            IncognitoMode = false,
            AutoArchive = false,
            HistoryEnabled = false
        });
        var root = CreateTestRoot("ScopedPreferenceAcceptance");
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (_, _) => new CapturingClient("{\"kind\":\"final\",\"content\":\"我会按计划推进，并及时同步进度。\"}"))
        {
            UserInput = "请通知同事项目将在周五完成。",
            Scenario = "职场沟通"
        };

        try
        {
            await vm.OptimizeCommand.ExecuteAsync(null);
            Assert.False(vm.HasError, vm.ErrorMessage);
            Assert.Equal("我会按计划推进，并及时同步进度。", vm.OptimizedResult);
            vm.OptimizedResult = "我会按计划推进，并在周四向团队同步进度。";
            vm.SaveEditedResultCommand.Execute(null);

            Assert.Equal(1, App.Settings.ExpressionPreferenceProfile.InteractionSignals["polish|职场沟通"].EditCount);
            Assert.False(App.Settings.ExpressionPreferenceProfile.InteractionSignals.ContainsKey("polish"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(ApplicationMode.Polish, false, false)]
    [InlineData(ApplicationMode.Polish, true, true)]
    [InlineData(ApplicationMode.PromptOptimize, false, false)]
    [InlineData(ApplicationMode.PromptOptimize, true, true)]
    public async Task MainViewModel_CloudPreferenceDisclosureFollowsExplicitSetting(ApplicationMode task, bool allowCloud, bool preferenceIncluded)
    {
        var profile = new ProviderProfile
        {
            Id = "cloud-preference-test",
            Name = "测试云模型",
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            ApiBase = "https://api.example.test/v1",
            Model = "test-model"
        };
        var preferences = new ExpressionPreferenceProfile();
        preferences.ForbiddenExpressions = ["历史全局未确认禁用项"];
        preferences.TaskPreferences["polish"] = new ExpressionPreferenceSet
        {
            PreferredLength = "concise",
            UserConfirmed = true,
            Source = "user-confirmed",
            Confidence = 1
        };
        preferences.TaskPreferences["prompt-optimize|通用任务"] = new ExpressionPreferenceSet
        {
            PreferredLength = "concise",
            UserConfirmed = true,
            Source = "user-confirmed",
            Confidence = 1
        };
        App.ReplaceSettings(new AppSettings
        {
            ProviderProfiles = [profile],
            ActiveProviderProfileId = profile.Id,
            ExpressionPreferenceProfile = preferences,
            ShareConfirmedPreferencesWithCloud = allowCloud,
            IncognitoMode = true,
            AutoArchive = false,
            HistoryEnabled = false
        });
        var root = CreateTestRoot("CloudPreferenceDisclosure");
        var client = new CapturingClient("{\"kind\":\"final\",\"content\":\"改写结果\"}");
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (_, _) => client)
        {
            CurrentMode = task,
            UserInput = "请完成这个文本任务。"
        };

        try
        {
            await vm.OptimizeCommand.ExecuteAsync(null);

            Assert.False(vm.HasError, vm.ErrorMessage);
            if (preferenceIncluded)
            {
                Assert.Contains("偏好简洁、直接的成稿", client.SystemPrompts.Single(), StringComparison.Ordinal);
                Assert.DoesNotContain("历史全局未确认禁用项", client.SystemPrompts.Single(), StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain("偏好简洁、直接的成稿", client.SystemPrompts.Single(), StringComparison.Ordinal);
                Assert.DoesNotContain("历史全局未确认禁用项", client.SystemPrompts.Single(), StringComparison.Ordinal);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MainViewModel_LocalLoopbackUsesConfirmedPreferenceWithoutCloudConsent()
    {
        var profile = new ProviderProfile
        {
            Id = "local-preference-test",
            Name = "测试本地模型",
            Type = ProviderType.Local,
            Platform = ProviderPlatform.Ollama,
            ApiBase = "http://127.0.0.1:11434/v1",
            Model = "local-test-model"
        };
        var preferences = new ExpressionPreferenceProfile();
        preferences.ForbiddenExpressions = ["本机本地兼容表达"];
        preferences.TaskPreferences["polish"] = new ExpressionPreferenceSet
        {
            PreferredLength = "concise",
            UserConfirmed = true,
            Source = "user-confirmed",
            Confidence = 1
        };
        App.ReplaceSettings(new AppSettings
        {
            ProviderProfiles = [profile],
            ActiveProviderProfileId = profile.Id,
            ExpressionPreferenceProfile = preferences,
            ShareConfirmedPreferencesWithCloud = false,
            IncognitoMode = true,
            AutoArchive = false,
            HistoryEnabled = false
        });
        var root = CreateTestRoot("LocalPreferenceDisclosure");
        var client = new CapturingClient("{\"kind\":\"final\",\"content\":\"本地改写结果\"}");
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (_, _) => client)
        {
            CurrentMode = ApplicationMode.Polish,
            UserInput = "请润色这句话。"
        };

        try
        {
            await vm.OptimizeCommand.ExecuteAsync(null);

            Assert.False(vm.HasError, vm.ErrorMessage);
            Assert.Contains("偏好简洁、直接的成稿", client.SystemPrompts.Single(), StringComparison.Ordinal);
            Assert.Contains("本机本地兼容表达", client.SystemPrompts.Single(), StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MainViewModel_DoesNotExposeConfirmedPreferenceToConfiguredCloudFallbackByDefault()
    {
        var local = new ProviderProfile
        {
            Id = "local-primary",
            Name = "本机首选",
            Type = ProviderType.Local,
            Platform = ProviderPlatform.Ollama,
            ApiBase = "http://localhost:11434/v1",
            Model = "local-model"
        };
        var cloud = new ProviderProfile
        {
            Id = "cloud-backup",
            Name = "云端备用",
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.OpenAI,
            ApiBase = "https://api.example.test/v1",
            Model = "cloud-model"
        };
        var preferences = new ExpressionPreferenceProfile();
        preferences.TaskPreferences["polish"] = new ExpressionPreferenceSet
        {
            PreferredLength = "concise",
            UserConfirmed = true,
            Source = "user-confirmed",
            Confidence = 1
        };
        App.ReplaceSettings(new AppSettings
        {
            ProviderProfiles = [local, cloud],
            ActiveProviderProfileId = cloud.Id,
            ProviderRoutingMode = ProviderRoutingMode.PreferLocal,
            PolishProviderProfileId = local.Id,
            FallbackProviderProfileId = cloud.Id,
            ExpressionPreferenceProfile = preferences,
            IncognitoMode = true,
            AutoArchive = false,
            HistoryEnabled = false
        });
        var root = CreateTestRoot("FallbackPreferenceDisclosure");
        var cloudClient = new CapturingClient("{\"kind\":\"final\",\"content\":\"备用改写结果\"}");
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (selected, _) => selected.Id == local.Id
                ? new ThrowingClient(new GenerationFailureException(GenerationFailureKind.Timeout, "本地模型不可用", isTransient: true))
                : cloudClient)
        {
            UserInput = "请润色这句话。"
        };

        try
        {
            await vm.OptimizeCommand.ExecuteAsync(null);

            Assert.False(vm.HasError, vm.ErrorMessage);
            Assert.Equal("备用改写结果", vm.OptimizedResult);
            Assert.DoesNotContain("偏好简洁、直接的成稿", cloudClient.SystemPrompts.Single(), StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PolishWorkflow_LocalModeDoesNotDecorateTheExistingPrompt()
    {
        App.ReplaceSettings(new AppSettings { CompanionDriverMode = CompanionDriverMode.Local, IncognitoMode = true });
        var request = new PolishRequest { OriginalText = "帮我改写这句话" };
        var builder = new PolishPromptBuilderService();
        var client = new CapturingClient("{\"kind\":\"final\",\"content\":\"改写结果\"}");
        var root = CreateTestRoot("CompanionLocalWorkflow");

        try
        {
            var result = await new PolishWorkflowService(client, builder, new ArchiveService(root))
                .ExecuteAsync(request, false, false, null, DateTimeOffset.UtcNow);

            Assert.StartsWith(builder.BuildSystemPrompt(request, false), client.SystemPrompts.Single());
            Assert.Contains("<output_contract>", client.SystemPrompts.Single());
            Assert.DoesNotContain("HUAXIAZI_EMOTION", client.SystemPrompts.Single());
            Assert.Null(result.CompanionEmotion);
            Assert.Equal(1, client.Calls);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PolishWorkflow_EmotionModeUsesTheSameCallAndReturnsTheAcceptedHint()
    {
        App.ReplaceSettings(new AppSettings { CompanionDriverMode = CompanionDriverMode.EmotionAssistant, IncognitoMode = true });
        var response = "{\"kind\":\"final\",\"content\":\"我理解你的顾虑。\"}\n" +
                       "<HUAXIAZI_EMOTION>{\"emotion\":\"Supportive\",\"intensity\":0.7}</HUAXIAZI_EMOTION>";
        var client = new CapturingClient(response);
        var root = CreateTestRoot("CompanionEmotionWorkflow");

        try
        {
            var result = await new PolishWorkflowService(client, new PolishPromptBuilderService(), new ArchiveService(root))
                .ExecuteAsync(new PolishRequest { OriginalText = "我有点担心延期" }, false, false, null, DateTimeOffset.UtcNow);

            Assert.Equal(1, client.Calls);
            Assert.Contains("HUAXIAZI_EMOTION", client.SystemPrompts.Single());
            Assert.Equal("我理解你的顾虑。", result.Response.Content);
            Assert.Equal(AssistantEmotionKind.Supportive, result.CompanionEmotion?.Emotion);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PolishWorkflow_UsesExplicitCompanionModeForReproducibleRuns()
    {
        App.ReplaceSettings(new AppSettings { CompanionDriverMode = CompanionDriverMode.Local, IncognitoMode = true });
        var response = "{\"kind\":\"final\",\"content\":\"我理解你的顾虑。\"}\n" +
                       "<HUAXIAZI_EMOTION>{\"emotion\":\"Supportive\",\"intensity\":0.7}</HUAXIAZI_EMOTION>";
        var client = new CapturingClient(response);
        var root = CreateTestRoot("CompanionInjectedWorkflow");

        try
        {
            var result = await new PolishWorkflowService(client, new PolishPromptBuilderService(), new ArchiveService(root),
                    () => CompanionDriverMode.EmotionAssistant)
                .ExecuteAsync(new PolishRequest { OriginalText = "我有点担心延期" }, false, false, null, DateTimeOffset.UtcNow);

            Assert.Contains("HUAXIAZI_EMOTION", client.SystemPrompts.Single());
            Assert.Equal(AssistantEmotionKind.Supportive, result.CompanionEmotion?.Emotion);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AIService_BadRequestThrowsStructuredFailureWithoutPersistingProviderDetails()
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            ReasonPhrase = "Bad Request",
            Content = new StringContent("{\"error\":{\"message\":\"invalid input\"}}", Encoding.UTF8, "application/json")
        };
        using var service = new AIService(new ProviderProfile(), "test-key", new StaticHandler(response));

        var error = await Assert.ThrowsAsync<GenerationFailureException>(
            () => service.GenerateAsync("system", "user"));

        Assert.Equal(GenerationFailureKind.RequestRejected, error.Kind);
        Assert.Equal(HttpStatusCode.BadRequest, error.HttpStatusCode);
        Assert.DoesNotContain("invalid input", error.Message);
    }

    [Fact]
    public async Task PromptOptimization_EmotionModeUsesTheAcceptedResultWithoutAnotherRequest()
    {
        App.ReplaceSettings(new AppSettings { CompanionDriverMode = CompanionDriverMode.EmotionAssistant });
        var client = new CapturingClient(
            "请生成一份结构清晰、可直接执行的项目周报。\n" +
            "<HUAXIAZI_EMOTION>{\"emotion\":\"Encouraging\",\"intensity\":0.6}</HUAXIAZI_EMOTION>");
        var request = PromptRequest.Create("请生成一份项目周报", PromptCategory.General, PromptDepth.Standard);
        var plan = new ProfessionalizationPlanner().Create(new ProfessionalizationRequest
        {
            Input = request.UserInput,
            Mode = ApplicationMode.PromptOptimize,
            Category = request.Category,
            Depth = request.Depth
        });

        var result = await new PromptOptimizationWorkflowService(client, new PromptBuilderService())
            .ExecuteAsync(request, plan);

        Assert.Equal(1, client.Calls);
        Assert.Contains("HUAXIAZI_EMOTION", client.SystemPrompts.Single());
        Assert.DoesNotContain("HUAXIAZI_EMOTION", result.Content);
        Assert.Equal(AssistantEmotionKind.Encouraging, result.CompanionEmotion?.Emotion);
    }

    [Fact]
    public async Task PromptOptimization_UsesExplicitCompanionModeForReproducibleRuns()
    {
        App.ReplaceSettings(new AppSettings { CompanionDriverMode = CompanionDriverMode.Local });
        var client = new CapturingClient(
            "请生成一份结构清晰、可直接执行的项目周报。\n" +
            "<HUAXIAZI_EMOTION>{\"emotion\":\"Encouraging\",\"intensity\":0.6}</HUAXIAZI_EMOTION>");
        var request = PromptRequest.Create("请生成一份项目周报", PromptCategory.General, PromptDepth.Standard);
        var plan = new ProfessionalizationPlanner().Create(new ProfessionalizationRequest
        {
            Input = request.UserInput,
            Mode = ApplicationMode.PromptOptimize,
            Category = request.Category,
            Depth = request.Depth
        });

        var result = await new PromptOptimizationWorkflowService(client, new PromptBuilderService(),
                () => CompanionDriverMode.EmotionAssistant)
            .ExecuteAsync(request, plan);

        Assert.Contains("HUAXIAZI_EMOTION", client.SystemPrompts.Single());
        Assert.Equal(AssistantEmotionKind.Encouraging, result.CompanionEmotion?.Emotion);
    }

    [Theory]
    [InlineData(AssistantEmotionKind.Neutral, CompanionVisualState.Idle)]
    [InlineData(AssistantEmotionKind.Attentive, CompanionVisualState.Listening)]
    [InlineData(AssistantEmotionKind.Supportive, CompanionVisualState.Happy)]
    [InlineData(AssistantEmotionKind.Encouraging, CompanionVisualState.Happy)]
    [InlineData(AssistantEmotionKind.Concerned, CompanionVisualState.Warning)]
    [InlineData(AssistantEmotionKind.Cautious, CompanionVisualState.Curious)]
    public void AssistantEmotionMapsOnlyToSafeSemanticVisualStates(
        AssistantEmotionKind emotion,
        CompanionVisualState expected)
    {
        Assert.Equal(expected, CompanionEmotionMapper.ToVisualState(new AssistantEmotionHint(emotion, 0.7)));
    }

    [Fact]
    public async Task MainViewModel_KeepsStructuredFailureForLogsWhileShowingReadableFeedback()
    {
        App.ReplaceSettings(new AppSettings { IncognitoMode = true });
        var root = CreateTestRoot("CompanionFailureVm");
        var failure = new GenerationFailureException(
            GenerationFailureKind.Authentication,
            "HTTP 401：API Key 无效。",
            HttpStatusCode.Unauthorized,
            false);
        var vm = new MainViewModel(
            new WorkspaceDraftService(root),
            new ArchiveService(root),
            (_, _) => new ThrowingClient(failure))
        {
            UserInput = "请润色这句话"
        };

        try
        {
            await vm.OptimizeCommand.ExecuteAsync(null);

            Assert.Same(failure, vm.LastGenerationFailure);
            Assert.True(vm.HasError);
            Assert.Contains("API Key", vm.OperationalNotice);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class CapturingClient(params string[] responses) : ITextGenerationClient
    {
        private int _index;
        public int Calls => _index;
        public List<string> SystemPrompts { get; } = [];

        public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default)
        {
            SystemPrompts.Add(systemPrompt);
            var response = responses[Math.Min(_index, responses.Length - 1)];
            _index++;
            return Task.FromResult(response);
        }
    }

    private sealed class StructuredWorkflowDiagnosticsClient(ApplicationMode task) : ITextGenerationClient, IStructuredTextGenerationClient
    {
        public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default) =>
            Task.FromException<string>(new InvalidOperationException("测试应使用结构化输出路径。"));

        public Task<string> GenerateStructuredAsync(string systemPrompt, string userInput, string jsonSchema, CancellationToken cancellationToken = default) =>
            Task.FromResult(task == ApplicationMode.Polish
                ? "{\"kind\":\"final\",\"scenario\":\"其他\",\"topic\":\"进度同步\",\"content\":\"我会在周五前完成，并及时同步进展。\",\"questions\":[]}"
                : "{\"answer\":\"请设计一个支持3个仓库的库存系统，明确数据模型、核心流程、异常处理和验收标准，并给出可验证的实施步骤。\"}");
    }

    private sealed class StaticHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class ThrowingClient(Exception error) : ITextGenerationClient
    {
        public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default) =>
            Task.FromException<string>(error);
    }
}
