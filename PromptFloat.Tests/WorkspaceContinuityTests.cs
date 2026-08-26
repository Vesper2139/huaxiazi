using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFloat.Models;
using PromptFloat.Services;
using PromptFloat.ViewModels;
using Xunit;

namespace PromptFloat.Tests;

public sealed class WorkspaceContinuityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HuaxiaziWorkspace_" + Guid.NewGuid().ToString("N"));

    private sealed class DeferredGenerationClient : ITextGenerationClient
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => _started.Task;
        public void Complete(string response) => _response.TrySetResult(response);
        public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            return _response.Task; // 模拟忽略取消信号、最终仍返回内容的第三方客户端。
        }
    }

    [Fact]
    public async Task CancelledRequest_WhenProviderReturnsLate_DoesNotWriteStaleResult()
    {
        var client = new DeferredGenerationClient();
        App.ReplaceSettings(new AppSettings
        {
            DefaultMode = ApplicationMode.PromptOptimize,
            AutoArchive = false,
            HistoryEnabled = false,
            ProviderProfiles =
            [
                new ProviderProfile
                {
                    Id = "local", Name = "本机测试", Type = ProviderType.Local,
                    ApiBase = "http://localhost:11434/v1", Model = "test-model"
                }
            ],
            ActiveProviderProfileId = "local"
        });
        var vm = new MainViewModel(
            new WorkspaceDraftService(_root),
            new ArchiveService(_root),
            (_, _) => client)
        {
            UserInput = "不要让这次返回污染工作区"
        };

        var request = vm.OptimizeCommand.ExecuteAsync(null);
        await client.Started;
        vm.CancelCommand.Execute(null);
        client.Complete("迟到的旧结果");
        await request;

        Assert.Empty(vm.OptimizedResult);
        Assert.Equal(ViewMode.Original, vm.ViewMode);
        Assert.Equal("已取消", vm.ArchiveStatus);
    }

    [Fact]
    public void WorkspaceDraftStore_RoundTripsCompleteEditableState()
    {
        var store = new WorkspaceDraftService(_root);
        var draft = new WorkspaceDraft
        {
            UserInput = "原文",
            OptimizedResult = "优化稿",
            CurrentMode = ApplicationMode.PromptOptimize,
            ViewMode = ViewMode.Optimized,
            SelectedCategory = PromptCategory.Coding,
            SelectedDepth = PromptDepth.Detailed,
            ActiveProviderProfileId = "deepseek",
            Recipient = "研发团队",
            UpdatedAt = new DateTimeOffset(2026, 8, 14, 8, 0, 0, TimeSpan.Zero)
        };

        store.Save(draft);
        var restored = store.Load();

        Assert.NotNull(restored);
        Assert.Equal("原文", restored!.UserInput);
        Assert.Equal("优化稿", restored.OptimizedResult);
        Assert.Equal(ApplicationMode.PromptOptimize, restored.CurrentMode);
        Assert.Equal(ViewMode.Optimized, restored.ViewMode);
        Assert.Equal(PromptCategory.Coding, restored.SelectedCategory);
        Assert.Equal(PromptDepth.Detailed, restored.SelectedDepth);
        Assert.Equal("deepseek", restored.ActiveProviderProfileId);
        Assert.Equal("研发团队", restored.Recipient);
    }

    [Fact]
    public void WorkspaceDraftStore_CorruptFileReturnsNoDraftAndPreservesEvidence()
    {
        var store = new WorkspaceDraftService(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "workspace-draft.json"), "{broken");

        var restored = store.Load();

        Assert.Null(restored);
        Assert.True(File.Exists(Path.Combine(_root, "workspace-draft.corrupt.json")));
    }

    [Fact]
    public void SwitchingMode_PreservesCurrentInputAndResult()
    {
        App.ReplaceSettings(new AppSettings());
        var vm = new MainViewModel(new WorkspaceDraftService(_root), new ArchiveService(_root));
        vm.UserInput = "不能丢失的输入";
        vm.OptimizedResult = "不能丢失的结果";

        vm.SelectModeCommand.Execute(ApplicationMode.PromptOptimize);

        Assert.Equal("不能丢失的输入", vm.UserInput);
        Assert.Equal("不能丢失的结果", vm.OptimizedResult);
    }

    [Fact]
    public void SaveDraftIfDirty_PersistsAndNextViewModelRestoresWorkspace()
    {
        App.ReplaceSettings(new AppSettings());
        var store = new WorkspaceDraftService(_root);
        var first = new MainViewModel(store, new ArchiveService(_root));
        first.UserInput = "自动保存原文";
        first.OptimizedResult = "自动保存结果";

        first.SaveDraftIfDirty();
        var restored = new MainViewModel(store, new ArchiveService(_root));

        Assert.Equal("自动保存原文", restored.UserInput);
        Assert.Equal("自动保存结果", restored.OptimizedResult);
    }

    [Fact]
    public void IncognitoMode_DoesNotWriteRecoverableWorkspaceDraft()
    {
        App.ReplaceSettings(new AppSettings { IncognitoMode = true });
        var store = new WorkspaceDraftService(_root);
        var vm = new MainViewModel(store, new ArchiveService(_root)) { UserInput = "敏感临时内容" };

        vm.SaveDraftIfDirty();

        Assert.Null(store.Load());
        Assert.Contains("无痕", vm.DraftStatus);
    }

    [Fact]
    public void ClearThenUndoAndRedo_RestoresBothSidesOfWorkspace()
    {
        App.ReplaceSettings(new AppSettings());
        var vm = new MainViewModel(new WorkspaceDraftService(_root), new ArchiveService(_root));
        vm.UserInput = "待恢复原文";
        vm.OptimizedResult = "待恢复成稿";

        vm.ClearInputCommand.Execute(null);
        vm.UndoWorkspaceCommand.Execute(null);

        Assert.Equal("待恢复原文", vm.UserInput);
        Assert.Equal("待恢复成稿", vm.OptimizedResult);
        Assert.True(vm.CanRedoWorkspace);

        vm.RedoWorkspaceCommand.Execute(null);
        Assert.Empty(vm.UserInput);
        Assert.Empty(vm.OptimizedResult);
    }

    [Fact]
    public void SelectProvider_ChangesTheModelUsedByTheNextRequestWithoutRestart()
    {
        App.ReplaceSettings(new AppSettings
        {
            ProviderProfiles =
            [
                new ProviderProfile { Id = "openai", Name = "OpenAI" },
                new ProviderProfile { Id = "deepseek", Name = "DeepSeek", Model = "deepseek-chat" }
            ],
            ActiveProviderProfileId = "openai"
        });
        var vm = new MainViewModel(new WorkspaceDraftService(_root), new ArchiveService(_root));

        vm.SelectProviderCommand.Execute(App.Settings.ProviderProfiles[1]);

        Assert.Equal("deepseek", App.Settings.ActiveProviderProfileId);
        Assert.Equal("DeepSeek · deepseek-chat", vm.ActiveProviderLabel);
    }

    [Fact]
    public void LoadRevision_RestoresSavedResultAsAnEditableDraft()
    {
        App.ReplaceSettings(new AppSettings());
        var archive = new ArchiveService(_root);
        var revision = archive.SavePolishRevision(new ArchiveDraft
        {
            OriginalText = "历史原文",
            FinalText = "历史成稿",
            Topic = "历史主题"
        }, DateTimeOffset.UtcNow);
        var vm = new MainViewModel(new WorkspaceDraftService(_root), archive);

        vm.LoadRevisionCommand.Execute(revision);

        Assert.Equal("历史原文", vm.UserInput);
        Assert.Equal("历史成稿", vm.OptimizedResult);
        Assert.Equal(ViewMode.Optimized, vm.ViewMode);
        Assert.True(vm.IsEditingResult);
        Assert.False(vm.IsReadOnly);
        Assert.Equal("历史成稿", vm.DisplayText);
    }

    [Fact]
    public async Task LoadRevision_DuringGeneration_CancelsStaleRequestAndRestoresRevisionMode()
    {
        var client = new DeferredGenerationClient();
        App.ReplaceSettings(new AppSettings
        {
            DefaultMode = ApplicationMode.Polish,
            AutoArchive = false,
            HistoryEnabled = false,
            ProviderProfiles =
            [
                new ProviderProfile
                {
                    Id = "local", Name = "本机测试", Type = ProviderType.Local,
                    ApiBase = "http://localhost:11434/v1", Model = "test-model"
                }
            ],
            ActiveProviderProfileId = "local"
        });
        var vm = new MainViewModel(
            new WorkspaceDraftService(_root),
            new ArchiveService(_root),
            (_, _) => client)
        {
            UserInput = "正在处理的内容"
        };
        var request = vm.OptimizeCommand.ExecuteAsync(null);
        await client.Started;
        var revision = new ContentRevision
        {
            Id = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            OriginalText = "历史原文",
            FinalText = "历史提示词",
            Mode = ApplicationMode.PromptOptimize
        };

        vm.LoadRevisionCommand.Execute(revision);
        client.Complete("迟到结果");
        await request;

        Assert.False(vm.IsBusy);
        Assert.Equal(ApplicationMode.PromptOptimize, vm.CurrentMode);
        Assert.Equal("历史原文", vm.UserInput);
        Assert.Equal("历史提示词", vm.OptimizedResult);
    }

    [Fact]
    public void Archive_PreservesPromptOptimizationModeForHistoryFiltering()
    {
        var archive = new ArchiveService(_root);

        var revision = archive.SaveRevision(new ArchiveDraft
        {
            Mode = ApplicationMode.PromptOptimize,
            OriginalText = "原始提示词",
            FinalText = "结构化提示词"
        }, DateTimeOffset.UtcNow);

        Assert.Equal(ApplicationMode.PromptOptimize, revision.Mode);
    }

    [Fact]
    public void SelectingPreset_AppliesItsModeCategoryAndDepthToWorkspace()
    {
        var preset = new OptimizationPreset
        {
            Id = "academic", Name = "学术", Mode = ApplicationMode.PromptOptimize,
            Category = PromptCategory.Research, Depth = PromptDepth.Detailed
        };
        App.ReplaceSettings(new AppSettings { OptimizationPresets = [preset] });
        var vm = new MainViewModel(new WorkspaceDraftService(_root), new ArchiveService(_root));

        vm.SelectedPreset = preset;

        Assert.Equal(ApplicationMode.PromptOptimize, vm.CurrentMode);
        Assert.Equal(PromptCategory.Research, vm.SelectedCategory);
        Assert.Equal(PromptDepth.Detailed, vm.SelectedDepth);
        Assert.Equal("academic", App.Settings.ActivePresetId);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch { }
    }
}
