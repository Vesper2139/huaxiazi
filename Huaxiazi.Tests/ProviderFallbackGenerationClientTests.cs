using System;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class ProviderFallbackGenerationClientTests
{
    [Fact]
    public async Task ExplicitFallback_RetriesFailedGenerationAndReportsActualProfile()
    {
        var primary = new StubClient(new GenerationFailureException(GenerationFailureKind.Timeout, "timeout", isTransient: true));
        var fallback = new StubClient("backup answer");
        var profile = new ProviderProfile { Id = "backup", Name = "Backup", Model = "backup-model" };
        ProviderProfile? actual = null;
        using var client = new ProviderFallbackGenerationClient(
            primary,
            profile,
            _ => fallback,
            used => actual = used);

        var result = await client.GenerateAsync("system", "input");

        Assert.Equal("backup answer", result);
        Assert.Same(profile, actual);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task NoConfiguredFallback_DoesNotRetryPrimaryFailure()
    {
        var failure = new GenerationFailureException(GenerationFailureKind.Timeout, "timeout", isTransient: true);
        var primary = new StubClient(failure);
        var factoryCalls = 0;
        using var client = new ProviderFallbackGenerationClient(primary, null, _ =>
        {
            factoryCalls++;
            return new StubClient("must not run");
        }, _ => { });

        var actual = await Assert.ThrowsAsync<GenerationFailureException>(() => client.GenerateAsync("system", "input"));

        Assert.Same(failure, actual);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task Refusal_DoesNotRetryAgainstConfiguredFallbackProvider()
    {
        var refusal = new GenerationFailureException(GenerationFailureKind.Refused, "refused");
        var primary = new StubClient(refusal);
        var fallback = new StubClient("backup answer");
        var factoryCalls = 0;
        var selectionCalls = 0;
        using var client = new ProviderFallbackGenerationClient(primary,
            new ProviderProfile { Id = "backup" },
            _ => { factoryCalls++; return fallback; },
            _ => selectionCalls++);

        var actual = await Assert.ThrowsAsync<GenerationFailureException>(() => client.GenerateAsync("system", "sensitive input"));

        Assert.Same(refusal, actual);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, factoryCalls);
        Assert.Equal(0, fallback.Calls);
        Assert.Equal(0, selectionCalls);
    }

    [Fact]
    public async Task StructuredOutputUnsupported_DoesNotSwitchProviderBeforeLocalSchemaFallback()
    {
        var unsupported = new GenerationFailureException(GenerationFailureKind.StructuredOutputUnsupported, "schema unsupported");
        var primary = new StubClient(unsupported);
        var fallback = new StubClient("backup answer");
        var factoryCalls = 0;
        using var client = new ProviderFallbackGenerationClient(primary,
            new ProviderProfile { Id = "backup" },
            _ => { factoryCalls++; return fallback; },
            _ => { });

        var actual = await Assert.ThrowsAsync<GenerationFailureException>(() =>
            client.GenerateStructuredAsync("system", "input", "{}"));

        Assert.Same(unsupported, actual);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, factoryCalls);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task OnceFallbackIsActive_SubsequentCallsStayOnTheSelectedBackup()
    {
        var primary = new StubClient(new GenerationFailureException(GenerationFailureKind.ProviderUnavailable, "offline"));
        var fallback = new StubClient("backup");
        using var client = new ProviderFallbackGenerationClient(primary,
            new ProviderProfile { Id = "backup" }, _ => fallback, _ => { });

        Assert.Equal("backup", await client.GenerateAsync("system", "first"));
        Assert.Equal("backup", await client.GenerateAsync("system", "second"));

        Assert.Equal(1, primary.Calls);
        Assert.Equal(2, fallback.Calls);
    }

    private sealed class StubClient : ITextGenerationClient
    {
        private readonly Exception? _failure;
        private readonly string _result;

        public StubClient(string result) => _result = result;
        public StubClient(Exception failure) { _failure = failure; _result = string.Empty; }
        public int Calls { get; private set; }

        public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default)
        {
            Calls++;
            return _failure is null ? Task.FromResult(_result) : Task.FromException<string>(_failure);
        }
    }
}
