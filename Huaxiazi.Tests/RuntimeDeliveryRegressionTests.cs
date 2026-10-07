using System;
using System.IO;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class RuntimeDeliveryRegressionTests
{
    [Fact]
    public void DataRoot_FallsBackToWritableDirectory_WhenDefaultRootIsNotUsable()
    {
        var tempRoot = Path.Combine(AppContext.BaseDirectory, "runtime-delivery-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var blockedDefault = Path.Combine(tempRoot, "blocked");
        File.WriteAllText(blockedDefault, "not-a-directory");
        var fallback = Path.Combine(tempRoot, "fallback");

        try
        {
            var resolved = Huaxiazi.Services.DataDirectoryPolicy.ResolveOrDefault(
                configuredPath: null,
                defaultRoot: blockedDefault,
                fallbackRoot: fallback);

            Assert.Equal(Path.GetFullPath(fallback), resolved);
            Assert.True(Directory.Exists(resolved));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
