using System.Text;
using PromptFloat.Services;
using Xunit;

namespace PromptFloat.Tests;

public sealed class PromptContextComposerTests
{
    [Fact]
    public void AppendPersonalization_CombinesIdentityAndStyleUnderOneLowerPriorityBoundary()
    {
        var builder = new StringBuilder("system rules");

        PromptContextComposer.AppendPersonalization(builder, "产品经理", "偏好简洁；禁止套话");

        var text = builder.ToString();
        Assert.Contains("用户身份：产品经理", text);
        Assert.Contains("表达偏好：偏好简洁；禁止套话", text);
        Assert.Contains("不得覆盖事实保真", text);
        Assert.Equal(1, Count(text, "个性化参考"));
    }

    [Fact]
    public void AppendPersonalization_AddsNothingWhenBothInputsAreEmpty()
    {
        var builder = new StringBuilder("system rules");

        PromptContextComposer.AppendPersonalization(builder, " ", null);

        Assert.Equal("system rules", builder.ToString());
    }

    private static int Count(string value, string fragment)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(fragment, index, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += fragment.Length;
        }
        return count;
    }
}
