using System.Windows.Input;
using PromptFloat.Services;
using Xunit;

namespace PromptFloat.Tests;

public sealed class HotkeyParserTests
{
    [Fact]
    public void ValidateBindings_RejectsConflictsAcrossDifferentActions()
    {
        var bindings = new System.Collections.Generic.Dictionary<GlobalHotkeyAction, string>
        {
            [GlobalHotkeyAction.ToggleWindow] = "Ctrl+Shift+P",
            [GlobalHotkeyAction.QuickPolish] = "Ctrl+Shift+P"
        };

        var result = HotkeyBindingSet.Validate(bindings);

        Assert.False(result.IsValid);
        Assert.Contains("冲突", result.ErrorMessage);
    }

    [Fact]
    public void ValidateBindings_UsesUserFacingActionNames()
    {
        var bindings = new System.Collections.Generic.Dictionary<GlobalHotkeyAction, string>
        {
            [GlobalHotkeyAction.QuickPolish] = "Ctrl+Alt+1",
            [GlobalHotkeyAction.QuickPromptOptimize] = "Ctrl+Alt+1"
        };

        var result = HotkeyBindingSet.Validate(bindings);

        Assert.Contains("快速润色", result.ErrorMessage);
        Assert.Contains("Prompt 优化", result.ErrorMessage);
        Assert.DoesNotContain(nameof(GlobalHotkeyAction.QuickPolish), result.ErrorMessage);
    }

    [Fact]
    public void ValidateBindings_AcceptsFourDistinctCustomActions()
    {
        var bindings = new System.Collections.Generic.Dictionary<GlobalHotkeyAction, string>
        {
            [GlobalHotkeyAction.ToggleWindow] = "Ctrl+Shift+P",
            [GlobalHotkeyAction.QuickPolish] = "Ctrl+Alt+1",
            [GlobalHotkeyAction.QuickPromptOptimize] = "Ctrl+Alt+2",
            [GlobalHotkeyAction.CopyResult] = "Ctrl+Alt+3"
        };

        Assert.True(HotkeyBindingSet.Validate(bindings).IsValid);
    }

    [Fact]
    public void RegisterBatch_WhenOneActionIsOccupied_StillAttemptsAndKeepsOtherActions()
    {
        var attempted = new System.Collections.Generic.List<GlobalHotkeyAction>();
        var bindings = new System.Collections.Generic.Dictionary<GlobalHotkeyAction, string>
        {
            [GlobalHotkeyAction.ToggleWindow] = "Ctrl+Shift+P",
            [GlobalHotkeyAction.QuickPolish] = "Ctrl+Alt+1",
            [GlobalHotkeyAction.CopyResult] = "Ctrl+Alt+3"
        };

        var result = HotkeyRegistrationBatch.Execute(bindings, (action, _) =>
        {
            attempted.Add(action);
            return action != GlobalHotkeyAction.QuickPolish;
        });

        Assert.Equal(3, attempted.Count);
        Assert.True(result.Actions[GlobalHotkeyAction.ToggleWindow].Succeeded);
        Assert.False(result.Actions[GlobalHotkeyAction.QuickPolish].Succeeded);
        Assert.True(result.Actions[GlobalHotkeyAction.CopyResult].Succeeded);
        Assert.False(result.AllSucceeded);
    }

    [Fact]
    public void RegisterBatch_UnconfiguredOptionalActions_DoNotCountAsFailures()
    {
        var result = HotkeyRegistrationBatch.Execute(
            new System.Collections.Generic.Dictionary<GlobalHotkeyAction, string>
            {
                [GlobalHotkeyAction.ToggleWindow] = "Ctrl+Shift+H",
                [GlobalHotkeyAction.QuickPolish] = "",
                [GlobalHotkeyAction.QuickPromptOptimize] = "",
                [GlobalHotkeyAction.CopyResult] = ""
            }, (_, _) => true);

        Assert.True(result.AllSucceeded);
        Assert.False(result.Actions[GlobalHotkeyAction.QuickPolish].IsConfigured);
        Assert.DoesNotContain("QuickPolish", result.Summary);
    }

    [Fact]
    public void RegisterBatch_NoConfiguredActions_IsAValidUserChoice()
    {
        var result = HotkeyRegistrationBatch.Execute(
            Enum.GetValues<GlobalHotkeyAction>().ToDictionary(action => action, _ => string.Empty),
            (_, _) => throw new InvalidOperationException("不应尝试注册空快捷键"));

        Assert.False(result.HasConfiguredActions);
        Assert.True(result.AllSucceeded);
        Assert.Equal("未配置全局快捷键", result.Summary);
    }
    [Fact]
    public void TryParse_ValidCombination_ReturnsModifiersAndKey()
    {
        var success = HotkeyParser.TryParse("Ctrl+Shift+P", out var combination);

        Assert.True(success);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, combination.Modifiers);
        Assert.Equal(Key.P, combination.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("P")]
    [InlineData("Ctrl+NotAKey")]
    public void TryParse_UnsafeOrInvalidCombination_ReturnsFalse(string text)
    {
        Assert.False(HotkeyParser.TryParse(text, out _));
    }
}
