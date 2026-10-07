using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class LocalModelFormattingTests
{
    [Theory]
    [InlineData(1_282_439_584L, "1.2 GB")]
    [InlineData(2_497_279_136L, "2.3 GB")]
    [InlineData(5_027_784_224L, "4.7 GB")]
    [InlineData(1_572_864L, "1.5 MB")]
    [InlineData(1_536L, "1.5 KB")]
    public void FormatModelPackageSize_UsesHumanReadableUnits(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSizeFormatter.FormatModelPackageSize(bytes));
    }

    [Fact]
    public void FormatModelPackageSize_DoesNotUseRawBytes()
    {
        var formatted = ByteSizeFormatter.FormatModelPackageSize(1_024);

        Assert.DoesNotContain("字节", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain(" B", formatted, StringComparison.Ordinal);
    }
}
