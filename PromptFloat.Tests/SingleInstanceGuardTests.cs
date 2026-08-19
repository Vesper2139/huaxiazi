using PromptFloat.Services;
using Xunit;
using System.Threading;

namespace PromptFloat.Tests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void BuildSessionScopedName_UsesLocalNamespaceAndRejectsGlobalSemantics()
    {
        var name = SingleInstanceGuard.BuildSessionScopedName("Vesper.Huaxiazi");

        Assert.Equal("Local\\Vesper.Huaxiazi", name);
        Assert.DoesNotContain("Global\\", name);
    }

    [Fact]
    public void Acquire_SameName_AllowsOnlyOneOwnerAndSignalsExistingInstance()
    {
        var name = "Vesper.Tests." + Guid.NewGuid().ToString("N");
        var firstResult = SingleInstanceGuard.Acquire(name);
        Assert.Equal(SingleInstanceAcquireStatus.Acquired, firstResult.Status);
        using var activated = new ManualResetEventSlim();
        firstResult.Guard!.ActivationRequested += (_, _) => activated.Set();
        try
        {
            var secondResult = SingleInstanceGuard.Acquire(name);

            Assert.Equal(SingleInstanceAcquireStatus.AlreadyRunning, secondResult.Status);
            Assert.Null(secondResult.Guard);
            Assert.True(activated.Wait(TimeSpan.FromSeconds(2)));
        }
        finally { firstResult.Guard?.Dispose(); }
    }

    [Fact]
    public void Acquire_WhenMutexCreationIsDenied_ReturnsUnavailableInsteadOfThrowing()
    {
        var result = SingleInstanceGuard.Acquire(
            "Vesper.Tests.Denied",
            _ => throw new UnauthorizedAccessException("denied"));

        Assert.Equal(SingleInstanceAcquireStatus.Unavailable, result.Status);
        Assert.Null(result.Guard);
        Assert.Contains("无法启用单实例保护", result.Message);
    }
}
