using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class StructuredGenerationWorkflowTests
{
    [Fact]
    public async Task ExplicitSchemaParameterRejection_FallsBackToPromptAndLocalValidation()
    {
        var client = new StubStructuredClient(new GenerationFailureException(
            GenerationFailureKind.StructuredOutputUnsupported,
            "provider does not support this schema",
            HttpStatusCode.BadRequest));
        var workflow = new StructuredGenerationWorkflow(client, static (_, _) => Task.CompletedTask);

        var result = await workflow.ExecuteAsync("system", "user", CreateContract());

        Assert.True(result.Succeeded);
        Assert.Equal("answer", result.Answer);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, client.StructuredCalls);
        Assert.Equal(1, client.PlainCalls);
        Assert.Contains("<output_contract>", client.LastPlainSystemPrompt);
    }

    [Fact]
    public async Task AutomaticRoute_SchemaRejectionFallsBackLocallyWithoutSwitchingProvider()
    {
        var primary = new StubStructuredClient(new GenerationFailureException(
            GenerationFailureKind.StructuredOutputUnsupported,
            "provider does not support this schema",
            HttpStatusCode.BadRequest));
        var fallbackFactoryCalls = 0;
        using var routedClient = new ProviderFallbackGenerationClient(
            primary,
            new ProviderProfile { Id = "explicit-backup", Model = "backup-model" },
            _ =>
            {
                fallbackFactoryCalls++;
                return new StubStructuredClient(new GenerationFailureException(
                    GenerationFailureKind.ProviderUnavailable,
                    "fallback must not run"));
            },
            _ => { });
        var workflow = new StructuredGenerationWorkflow(routedClient, static (_, _) => Task.CompletedTask);

        var result = await workflow.ExecuteAsync("system", "user", CreateContract());

        Assert.True(result.Succeeded);
        Assert.Equal("answer", result.Answer);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, primary.StructuredCalls);
        Assert.Equal(1, primary.PlainCalls);
        Assert.Contains("<output_contract>", primary.LastPlainSystemPrompt);
        Assert.Equal(0, fallbackFactoryCalls);
    }

    [Fact]
    public async Task NativeSchemaRejection_IsRememberedAcrossOneRepairOnTheSameWorkflow()
    {
        var client = new StatefulSchemaRejectingClient("not-json", "{\"answer\":\"answer\"}");
        var workflow = new StructuredGenerationWorkflow(client, static (_, _) => Task.CompletedTask);

        var initial = await workflow.ExecuteAsync("system", "user", CreateContract(), maxAttempts: 1);
        var repaired = await workflow.ExecuteAsync("repair system", "user", CreateContract(), maxAttempts: 1);

        Assert.False(initial.Succeeded);
        Assert.True(repaired.Succeeded);
        Assert.Equal(1, client.StructuredCalls);
        Assert.Equal(2, client.PlainCalls);
        Assert.Contains("<output_contract>", client.LastPlainSystemPrompt);
    }

    [Theory]
    [InlineData(GenerationFailureKind.RequestRejected, HttpStatusCode.BadRequest)]
    [InlineData(GenerationFailureKind.Authentication, HttpStatusCode.Unauthorized)]
    public async Task OtherRequestFailures_AreNotRetriedAsSchemaFallback(GenerationFailureKind kind, HttpStatusCode statusCode)
    {
        var client = new StubStructuredClient(new GenerationFailureException(kind, "request failed", statusCode));
        var workflow = new StructuredGenerationWorkflow(client, static (_, _) => Task.CompletedTask);

        var error = await Assert.ThrowsAsync<GenerationFailureException>(() =>
            workflow.ExecuteAsync("system", "user", CreateContract()));

        Assert.Equal(kind, error.Kind);
        Assert.Equal(1, client.StructuredCalls);
        Assert.Equal(0, client.PlainCalls);
    }

    private static StructuredOutputContract CreateContract() => new(
        "answer",
        new HashSet<string>(StringComparer.Ordinal) { "answer" },
        new HashSet<string>(StringComparer.Ordinal) { "answer" });

    private sealed class StubStructuredClient(GenerationFailureException structuredFailure) : ITextGenerationClient, IStructuredTextGenerationClient
    {
        public int StructuredCalls { get; private set; }
        public int PlainCalls { get; private set; }
        public string LastPlainSystemPrompt { get; private set; } = string.Empty;

        public Task<string> GenerateStructuredAsync(string systemPrompt, string userInput, string jsonSchema, CancellationToken cancellationToken = default)
        {
            StructuredCalls++;
            return Task.FromException<string>(structuredFailure);
        }

        public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default)
        {
            PlainCalls++;
            LastPlainSystemPrompt = systemPrompt;
            return Task.FromResult("{\"answer\":\"answer\"}");
        }
    }

    private sealed class StatefulSchemaRejectingClient(params string[] textResponses) : ITextGenerationClient, IStructuredTextGenerationClient
    {
        private int _textIndex;
        public int StructuredCalls { get; private set; }
        public int PlainCalls { get; private set; }
        public string LastPlainSystemPrompt { get; private set; } = string.Empty;

        public Task<string> GenerateStructuredAsync(string systemPrompt, string userInput, string jsonSchema, CancellationToken cancellationToken = default)
        {
            StructuredCalls++;
            return Task.FromException<string>(new GenerationFailureException(
                GenerationFailureKind.StructuredOutputUnsupported, "schema unsupported", HttpStatusCode.BadRequest));
        }

        public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default)
        {
            PlainCalls++;
            LastPlainSystemPrompt = systemPrompt;
            var response = textResponses[Math.Min(_textIndex++, textResponses.Length - 1)];
            return Task.FromResult(response);
        }
    }
}
