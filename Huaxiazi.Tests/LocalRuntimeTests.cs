using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class LocalRuntimeTests
{
    [Fact]
    public void RuntimeRoot_UsesUserWritableInstallRoot_AndPreservesLegacyRuntimeFallback()
    {
        var root = CreateTestRoot("RuntimeRoot");
        var installRoot = Path.Combine(root, "user-data", "runtimes", "local");
        var legacyRoot = Path.Combine(root, "program-files", "runtimes", "local");
        try
        {
            Assert.Equal(installRoot, LocalRuntimePaths.ResolveRuntimeRoot(installRoot, legacyRoot));

            Directory.CreateDirectory(Path.Combine(legacyRoot, "cpu"));
            File.WriteAllText(Path.Combine(legacyRoot, "cpu", "llama-server.exe"), "legacy runtime marker");
            Assert.Equal(legacyRoot, LocalRuntimePaths.ResolveRuntimeRoot(installRoot, legacyRoot));

            Directory.CreateDirectory(Path.Combine(installRoot, "vulkan"));
            File.WriteAllText(Path.Combine(installRoot, "vulkan", "llama-server.exe"), "managed runtime marker");
            Assert.Equal(installRoot, LocalRuntimePaths.ResolveRuntimeRoot(installRoot, legacyRoot));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildStartInfo_UsesEnvironmentForApiKeyAndDisablesWebUiAndTools()
    {
        var options = new LocalRuntimeOptions
        {
            ContextSize = 8192,
            CpuThreads = 6,
            GpuMode = LocalGpuMode.Auto,
            BatchSize = 256,
            AdapterScale = 0.75
        };

        var startInfo = LocalRuntimeCommandBuilder.BuildStartInfo(
            @"C:\Program Files\Huaxiazi\llama-server.exe",
            @"C:\Users\User Name\model --tools all.gguf",
            @"C:\Users\User Name\polish adapter.gguf",
            options,
            port: 51821,
            apiKey: "ephemeral-secret",
            useVulkan: true);

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(@"C:\Program Files\Huaxiazi\llama-server.exe", startInfo.FileName);
        Assert.Contains("--no-webui", startInfo.ArgumentList);
        Assert.DoesNotContain("--tools", startInfo.ArgumentList);
        Assert.Contains(@"C:\Users\User Name\model --tools all.gguf", startInfo.ArgumentList);
        Assert.Equal("127.0.0.1", ValueAfter(startInfo.ArgumentList, "--host"));
        Assert.Equal("51821", ValueAfter(startInfo.ArgumentList, "--port"));
        Assert.DoesNotContain("--api-key", startInfo.ArgumentList);
        Assert.Equal("ephemeral-secret", startInfo.Environment[LocalRuntimeCommandBuilder.ApiKeyEnvironmentVariable]);
        Assert.Equal("all", ValueAfter(startInfo.ArgumentList, "--gpu-layers"));
        Assert.Equal("4", ValueAfter(startInfo.ArgumentList, "--verbosity"));
        Assert.Contains("error", startInfo.Environment["VK_LOADER_DEBUG"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("0.75", ValueAfter(startInfo.ArgumentList, "--lora-scaled").Split(':').Last());
    }

    [Fact]
    public void BuildStartInfo_CpuCandidateDoesNotEnableVerboseBackendDiagnostics()
    {
        var startInfo = LocalRuntimeCommandBuilder.BuildStartInfo(
            @"C:\runtime\llama-server.exe",
            @"C:\models\model.gguf",
            null,
            new LocalRuntimeOptions { GpuMode = LocalGpuMode.Off },
            port: 51821,
            apiKey: "ephemeral-secret",
            useVulkan: false);

        Assert.Equal("0", ValueAfter(startInfo.ArgumentList, "--gpu-layers"));
        Assert.DoesNotContain("--verbosity", startInfo.ArgumentList);
        Assert.DoesNotContain("VK_LOADER_DEBUG", startInfo.Environment.Keys);
    }

    [Theory]
    [InlineData(null, "error")]
    [InlineData("warn", "warn,error")]
    [InlineData("all", "all")]
    [InlineData("warn,error", "warn,error")]
    public void EnsureVulkanLoaderErrorLogging_PreservesConfiguredLevels(string? existing, string expected)
    {
        Assert.Equal(expected, LocalRuntimeCommandBuilder.EnsureVulkanLoaderErrorLogging(existing));
    }

    [Fact]
    public void RuntimeKey_ChangesWhenAdapterOrOptionsChange()
    {
        var options = new LocalRuntimeOptions { ContextSize = 4096 };
        var first = LocalRuntimeManager.BuildRuntimeKey("model@1", "model.gguf", null, options, useVulkan: false);

        options.ContextSize = 8192;
        var changedOptions = LocalRuntimeManager.BuildRuntimeKey("model@1", "model.gguf", null, options, useVulkan: false);
        var changedAdapter = LocalRuntimeManager.BuildRuntimeKey("model@1", "model.gguf", "adapter.gguf", options, useVulkan: false);

        Assert.NotEqual(first, changedOptions);
        Assert.NotEqual(changedOptions, changedAdapter);
    }

    [Fact]
    public void RuntimeCandidates_AutoPrefersVulkanWithCpuFallback_WhileGpuOffUsesCpuOnly()
    {
        var root = CreateTestRoot("RuntimeCandidates");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "vulkan"));
            Directory.CreateDirectory(Path.Combine(root, "cpu"));
            File.WriteAllText(Path.Combine(root, "vulkan", "llama-server.exe"), "vulkan runtime marker");
            File.WriteAllText(Path.Combine(root, "cpu", "llama-server.exe"), "cpu runtime marker");

            var automatic = LocalRuntimeManager.ResolveRuntimeCandidates(root, LocalGpuMode.Auto);
            var gpuOff = LocalRuntimeManager.ResolveRuntimeCandidates(root, LocalGpuMode.Off);

            Assert.Equal(
                [Path.Combine(root, "vulkan", "llama-server.exe"), Path.Combine(root, "cpu", "llama-server.exe")],
                automatic.Select(candidate => candidate.ExecutablePath));
            Assert.All(automatic, candidate => Assert.True(candidate.UseVulkan == candidate.ExecutablePath.Contains("vulkan", StringComparison.OrdinalIgnoreCase)));
            Assert.Equal([Path.Combine(root, "cpu", "llama-server.exe")], gpuOff.Select(candidate => candidate.ExecutablePath));
            Assert.False(Assert.Single(gpuOff).UseVulkan);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeStartup_FallsBackToCpuWhenVulkanFailsToBecomeReady()
    {
        var candidates = new[]
        {
            new LocalRuntimeCandidate(Path.Combine("vulkan", "llama-server.exe"), UseVulkan: true),
            new LocalRuntimeCandidate(Path.Combine("cpu", "llama-server.exe"), UseVulkan: false)
        };
        var attempted = new List<string>();

        var backend = await LocalRuntimeManager.StartWithFallbackAsync(
            candidates,
            (candidate, _) =>
            {
                attempted.Add(candidate.ExecutablePath);
                if (candidate.UseVulkan) throw new TimeoutException("Vulkan server did not become ready.");
                return Task.FromResult("cpu");
            },
            CancellationToken.None);

        Assert.Equal("cpu", backend);
        Assert.Equal([Path.Combine("vulkan", "llama-server.exe"), Path.Combine("cpu", "llama-server.exe")], attempted);
    }

    [Fact]
    public async Task RuntimeStartup_PreservesCpuFailureWhenVulkanAndCpuBothFail()
    {
        var candidates = new[]
        {
            new LocalRuntimeCandidate("vulkan/llama-server.exe", UseVulkan: true),
            new LocalRuntimeCandidate("cpu/llama-server.exe", UseVulkan: false)
        };
        var cpuFailure = new IOException("CPU runtime failed to start.");

        var actual = await Assert.ThrowsAsync<IOException>(() => LocalRuntimeManager.StartWithFallbackAsync<string>(
            candidates,
            (candidate, _) => candidate.UseVulkan
                ? throw new TimeoutException("Vulkan server did not become ready.")
                : throw cpuFailure,
            CancellationToken.None));

        Assert.Same(cpuFailure, actual);
        Assert.Equal("CPU runtime failed to start.", actual.Message);
    }

    [Fact]
    public async Task RuntimeStartup_DoesNotFallBackAfterUserCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var attempted = new List<string>();
        var candidates = new[]
        {
            new LocalRuntimeCandidate("vulkan/llama-server.exe", UseVulkan: true),
            new LocalRuntimeCandidate("cpu/llama-server.exe", UseVulkan: false)
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalRuntimeManager.StartWithFallbackAsync<string>(
            candidates,
            (candidate, _) =>
            {
                attempted.Add(candidate.ExecutablePath);
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            },
            cancellation.Token));

        Assert.Equal(["vulkan/llama-server.exe"], attempted);
    }

    [Fact]
    public async Task RuntimeHealthProbe_RetriesTransientHttpTimeout()
    {
        var attempts = 0;
        var handler = new DelegateHandler(_ =>
        {
            attempts++;
            return attempts == 1
                ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("health request timed out"))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var client = new HttpClient(handler);
        var endpoint = new LocalRuntimeEndpoint(new Uri("http://127.0.0.1:51821"), "secret", "qwen-test");

        await LocalRuntimeManager.WaitForReadyAsync(client, endpoint, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task RuntimeHealthProbe_DoesNotRetryWhenCallerCancels()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        var handler = new DelegateHandler(_ =>
        {
            attempts++;
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellation.Token);
        });
        using var client = new HttpClient(handler);
        var endpoint = new LocalRuntimeEndpoint(new Uri("http://127.0.0.1:51821"), "secret", "qwen-test");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LocalRuntimeManager.WaitForReadyAsync(client, endpoint, TimeSpan.FromSeconds(2), cancellation.Token));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task GenerateAsync_ManagedRuntimePreflightBlocksWhenInputAndOutputBudgetExceedContext()
    {
        var requests = new List<string>();
        var handler = new DelegateHandler(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"input_tokens\":3000}", Encoding.UTF8, "application/json")
            });
        });
        var observations = new List<ProviderRequestTelemetry>();
        var host = new StaticRuntimeHost(new LocalRuntimeEndpoint(
            new Uri("http://127.0.0.1:51821"), "local-secret", "qwen-test", supportsChatCompletionTokenCount: true));
        using var httpClient = new HttpClient(handler);
        using var client = new LocalTextGenerationClient(host, new ProviderProfile
        {
            Platform = ProviderPlatform.ManagedLocal,
            Type = ProviderType.Local,
            Model = "qwen-test",
            MaxTokens = 2048,
            LocalRuntimeOptions = new LocalRuntimeOptions { ContextSize = 4096 }
        }, httpClient, observations.Add);

        var error = await Assert.ThrowsAsync<GenerationFailureException>(() => client.GenerateAsync("system", "private user text"));

        Assert.Equal(GenerationFailureKind.ContextLimitExceeded, error.Kind);
        Assert.Contains("3000", error.Message);
        Assert.Contains("2048", error.Message);
        Assert.Equal(["/v1/chat/completions/input_tokens"], requests);
        var observation = Assert.Single(observations);
        Assert.Equal("context_limit_exceeded", observation.Outcome);
        Assert.Equal("exceeded", observation.ContextPreflightOutcome);
        Assert.Equal(3000, observation.InputTokens);
        Assert.Equal(200, observation.HttpStatusCode);
        Assert.DoesNotContain("private user text", System.Text.Json.JsonSerializer.Serialize(observation), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_ManagedRuntimePreflightCountsTheExactGenerationRequestAndAllowsItWhenItFits()
    {
        var paths = new List<string>();
        var bodies = new List<string>();
        var handler = new DelegateHandler(async request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            bodies.Add(await request.Content!.ReadAsStringAsync());
            var body = request.RequestUri.AbsolutePath.EndsWith("input_tokens", StringComparison.Ordinal)
                ? "{\"input_tokens\":1341}"
                : "{\"choices\":[{\"message\":{\"content\":\"本地结果\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1341,\"completion_tokens\":3}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });
        var host = new StaticRuntimeHost(new LocalRuntimeEndpoint(
            new Uri("http://127.0.0.1:51821"), "local-secret", "qwen-test", supportsChatCompletionTokenCount: true));
        var observations = new List<ProviderRequestTelemetry>();
        using var httpClient = new HttpClient(handler);
        using var client = new LocalTextGenerationClient(host, new ProviderProfile
        {
            Platform = ProviderPlatform.ManagedLocal,
            Type = ProviderType.Local,
            Model = "qwen-test",
            MaxTokens = 2048,
            LocalRuntimeOptions = new LocalRuntimeOptions { ContextSize = 4096 }
        }, httpClient, observations.Add);

        var result = await client.GenerateAsync("system prompt", "user input");

        Assert.Equal("本地结果", result);
        Assert.Equal(["/v1/chat/completions/input_tokens", "/v1/chat/completions"], paths);
        Assert.Equal(bodies[0], bodies[1]);
        Assert.Equal("counted", Assert.Single(observations).ContextPreflightOutcome);
    }

    [Fact]
    public async Task GenerateAsync_ManagedRuntimePreflightFailsOpenWhenCounterIsUnavailable()
    {
        var paths = new List<string>();
        var handler = new DelegateHandler(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            var response = request.RequestUri.AbsolutePath.EndsWith("input_tokens", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"本地结果\"}}]}", Encoding.UTF8, "application/json")
                };
            return Task.FromResult(response);
        });
        var host = new StaticRuntimeHost(new LocalRuntimeEndpoint(
            new Uri("http://127.0.0.1:51821"), "local-secret", "qwen-test", supportsChatCompletionTokenCount: true));
        var observations = new List<ProviderRequestTelemetry>();
        using var httpClient = new HttpClient(handler);
        using var client = new LocalTextGenerationClient(host, new ProviderProfile
        {
            Platform = ProviderPlatform.ManagedLocal,
            Type = ProviderType.Local,
            Model = "qwen-test",
            MaxTokens = 2048,
            LocalRuntimeOptions = new LocalRuntimeOptions { ContextSize = 4096 }
        }, httpClient, observations.Add);

        var result = await client.GenerateAsync("system prompt", "user input");

        Assert.Equal("本地结果", result);
        Assert.Equal(["/v1/chat/completions/input_tokens", "/v1/chat/completions"], paths);
        Assert.Equal("unavailable", Assert.Single(observations).ContextPreflightOutcome);
    }

    [Fact]
    public async Task GenerateAsync_ManagedRuntimePreflightCancellationIsObservableAndDoesNotGenerate()
    {
        using var cancellation = new CancellationTokenSource();
        var paths = new List<string>();
        var handler = new DelegateHandler(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellation.Token);
        });
        var observations = new List<ProviderRequestTelemetry>();
        var host = new StaticRuntimeHost(new LocalRuntimeEndpoint(
            new Uri("http://127.0.0.1:51821"), "local-secret", "qwen-test", supportsChatCompletionTokenCount: true));
        using var httpClient = new HttpClient(handler);
        using var client = new LocalTextGenerationClient(host, new ProviderProfile
        {
            Platform = ProviderPlatform.ManagedLocal,
            Type = ProviderType.Local,
            Model = "qwen-test",
            MaxTokens = 2048,
            LocalRuntimeOptions = new LocalRuntimeOptions { ContextSize = 4096 }
        }, httpClient, observations.Add);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GenerateAsync("system prompt", "user input", cancellation.Token));

        Assert.Equal(["/v1/chat/completions/input_tokens"], paths);
        var observation = Assert.Single(observations);
        Assert.Equal("cancelled", observation.ContextPreflightOutcome);
        Assert.Equal("cancelled", observation.Outcome);
    }

    [Fact]
    public void RuntimePackageCatalog_EnablesExactTokenCountOnlyForKnownVersionedBuilds()
    {
        var root = CreateTestRoot("TokenCountRuntimeCapability");
        try
        {
            var versioned = Path.Combine(root, "cpu", "versions", "b11424", "llama-server.exe");
            var versionedVulkan = Path.Combine(root, "vulkan", "versions", "b11424", "llama-server.exe");
            var unknownVersion = Path.Combine(root, "cpu", "versions", "b99999", "llama-server.exe");
            var legacy = Path.Combine(root, "cpu", "llama-server.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(versioned)!);
            Directory.CreateDirectory(Path.GetDirectoryName(versionedVulkan)!);
            Directory.CreateDirectory(Path.GetDirectoryName(unknownVersion)!);
            Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
            File.WriteAllText(versioned, "versioned");
            File.WriteAllText(versionedVulkan, "versioned-vulkan");
            File.WriteAllText(unknownVersion, "unknown-version");
            File.WriteAllText(legacy, "legacy");

            Assert.True(LocalRuntimePackageCatalog.SupportsChatCompletionTokenCountEndpoint(versioned));
            Assert.True(LocalRuntimePackageCatalog.SupportsChatCompletionTokenCountEndpoint(versionedVulkan));
            Assert.False(LocalRuntimePackageCatalog.SupportsChatCompletionTokenCountEndpoint(unknownVersion));
            Assert.False(LocalRuntimePackageCatalog.SupportsChatCompletionTokenCountEndpoint(legacy));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateStructuredAsync_UsesOnlyLoopbackRuntimeAndSendsJsonSchema()
    {
        Uri? requestedUri = null;
        string? authorization = null;
        string? body = null;
        var handler = new DelegateHandler(async request =>
        {
            requestedUri = request.RequestUri;
            authorization = request.Headers.Authorization?.Parameter;
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"本地结果\\\"}\"}}]}", Encoding.UTF8, "application/json")
            };
        });
        var host = new StaticRuntimeHost(new LocalRuntimeEndpoint(new Uri("http://127.0.0.1:51821"), "local-secret", "qwen-test"));
        var client = new LocalTextGenerationClient(host, new ProviderProfile
        {
            Platform = ProviderPlatform.ManagedLocal,
            Type = ProviderType.Local,
            Model = "qwen-test",
            Temperature = 0.3,
            TopP = 0.9,
            MaxTokens = 512,
            LocalModelInstallationId = "qwen-test@1"
        }, new HttpClient(handler));

        var result = await client.GenerateStructuredAsync("system", "user", "{\"type\":\"object\"}");

        Assert.Equal("{\"answer\":\"本地结果\"}", result);
        Assert.Equal("127.0.0.1", requestedUri?.Host);
        Assert.Equal("local-secret", authorization);
        Assert.Contains("response_format", body);
        Assert.Contains("json_schema", body);
    }

    [Fact]
    public async Task GenerateAsync_SendsConfiguredSeedAndRepeatPenalty()
    {
        string? body = null;
        var handler = new DelegateHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"本地结果\"}}]}", Encoding.UTF8, "application/json")
            };
        });
        var host = new StaticRuntimeHost(new LocalRuntimeEndpoint(new Uri("http://127.0.0.1:51821"), "local-secret", "qwen-test"));
        var client = new LocalTextGenerationClient(host, new ProviderProfile
        {
            Platform = ProviderPlatform.ManagedLocal,
            Type = ProviderType.Local,
            Model = "qwen-test",
            LocalModelInstallationId = "qwen-test@1",
            LocalRuntimeOptions = new LocalRuntimeOptions { Seed = 42, RepeatPenalty = 1.17 }
        }, new HttpClient(handler));

        var result = await client.GenerateAsync("system", "user");

        using var requestBody = System.Text.Json.JsonDocument.Parse(body!);
        Assert.Equal("本地结果", result);
        Assert.Equal(42, requestBody.RootElement.GetProperty("seed").GetInt32());
        Assert.Equal(1.17, requestBody.RootElement.GetProperty("repeat_penalty").GetDouble());
    }

    [Fact]
    public async Task GenerateAsync_StopsRuntimeAfterRequestWhenKeepLoadedIsDisabled()
    {
        var handler = new DelegateHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"本地结果\"}}]}", Encoding.UTF8, "application/json")
        }));
        var host = new StaticRuntimeHost(new LocalRuntimeEndpoint(new Uri("http://127.0.0.1:51821"), "local-secret", "qwen-test"));
        var client = new LocalTextGenerationClient(host, new ProviderProfile
        {
            Platform = ProviderPlatform.ManagedLocal,
            Type = ProviderType.Local,
            Model = "qwen-test",
            LocalModelInstallationId = "qwen-test@1",
            LocalRuntimeOptions = new LocalRuntimeOptions { KeepLoaded = false }
        }, new HttpClient(handler));

        _ = await client.GenerateAsync("system", "user");

        Assert.Equal(1, host.StopCalls);
    }

    [Fact]
    public async Task GenerateAsync_LeavesRuntimeRunningWhenKeepLoadedIsEnabled()
    {
        var handler = new DelegateHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"本地结果\"}}]}", Encoding.UTF8, "application/json")
        }));
        var host = new StaticRuntimeHost(new LocalRuntimeEndpoint(new Uri("http://127.0.0.1:51821"), "local-secret", "qwen-test"));
        var client = new LocalTextGenerationClient(host, new ProviderProfile
        {
            Platform = ProviderPlatform.ManagedLocal,
            Type = ProviderType.Local,
            Model = "qwen-test",
            LocalModelInstallationId = "qwen-test@1",
            LocalRuntimeOptions = new LocalRuntimeOptions { KeepLoaded = true }
        }, new HttpClient(handler));

        _ = await client.GenerateAsync("system", "user");

        Assert.Equal(0, host.StopCalls);
    }

    [Fact]
    public async Task GenerateAsync_StopsRuntimeAfterRejectedRequestWhenKeepLoadedIsDisabled()
    {
        var handler = new DelegateHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
        var host = new StaticRuntimeHost(new LocalRuntimeEndpoint(new Uri("http://127.0.0.1:51821"), "local-secret", "qwen-test"));
        var client = new LocalTextGenerationClient(host, new ProviderProfile
        {
            Platform = ProviderPlatform.ManagedLocal,
            Type = ProviderType.Local,
            Model = "qwen-test",
            LocalModelInstallationId = "qwen-test@1",
            LocalRuntimeOptions = new LocalRuntimeOptions { KeepLoaded = false }
        }, new HttpClient(handler));

        var failure = await Assert.ThrowsAsync<GenerationFailureException>(() => client.GenerateAsync("system", "user"));

        Assert.Equal(GenerationFailureKind.RequestRejected, failure.Kind);
        Assert.Equal(1, host.StopCalls);
    }

    [Fact]
    public async Task GenerateStructuredAsync_NormalizesSchemaRejectionForTextFallback()
    {
        var handler = new DelegateHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
        var host = new StaticRuntimeHost(new LocalRuntimeEndpoint(new Uri("http://127.0.0.1:51821"), "local-secret", "qwen-test"));
        var client = new LocalTextGenerationClient(host, new ProviderProfile
        {
            Platform = ProviderPlatform.ManagedLocal,
            Type = ProviderType.Local,
            Model = "qwen-test",
            LocalModelInstallationId = "qwen-test@1"
        }, new HttpClient(handler));

        var failure = await Assert.ThrowsAsync<GenerationFailureException>(() =>
            client.GenerateStructuredAsync("system", "user", "{\"type\":\"object\"}"));

        Assert.Equal(GenerationFailureKind.RequestRejected, failure.Kind);
        Assert.Equal(HttpStatusCode.BadRequest, failure.HttpStatusCode);
    }

    [Fact]
    public async Task GenerateStructuredAsync_ReportsOnlyRequestTimingAndProviderUsageWhenObserverIsConfigured()
    {
        var observations = new List<ProviderRequestTelemetry>();
        var handler = new DelegateHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"id\":\"req-local-1\",\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"result\\\"}\"}}],\"usage\":{\"prompt_tokens\":17,\"completion_tokens\":5}}",
                Encoding.UTF8,
                "application/json")
        }));
        var host = new StaticRuntimeHost(
            new LocalRuntimeEndpoint(new Uri("http://127.0.0.1:51821"), "local-secret", "qwen-test", backend: "Vulkan", confirmedBackend: "Vulkan"),
            new LocalRuntimeMetrics(321.5, 987654));
        var clientConstructor = typeof(LocalTextGenerationClient).GetConstructor(
            [typeof(ILocalRuntimeHost), typeof(ProviderProfile), typeof(HttpClient), typeof(Action<ProviderRequestTelemetry>)]);
        Assert.NotNull(clientConstructor);
        using var httpClient = new HttpClient(handler);
        using var client = (LocalTextGenerationClient)clientConstructor!.Invoke(
            [host, new ProviderProfile
            {
                Platform = ProviderPlatform.ManagedLocal,
                Type = ProviderType.Local,
                Model = "qwen-test",
                LocalModelInstallationId = "qwen-test@1"
            }, httpClient, (Action<ProviderRequestTelemetry>)observations.Add]);

        var result = await client.GenerateStructuredAsync("private-system", "private-user", "{\"type\":\"object\"}");

        Assert.Equal("{\"answer\":\"result\"}", result);
        var observation = Assert.Single(observations);
        Assert.Equal("req-local-1", observation.RequestId);
        Assert.Equal(17, observation.InputTokens);
        Assert.Equal(5, observation.OutputTokens);
        Assert.Equal(200, observation.HttpStatusCode);
        Assert.Equal("success", observation.Outcome);
        Assert.Equal("Vulkan", observation.RuntimeBackend);
        Assert.Equal(321.5, observation.RuntimeStartupToReadyMilliseconds);
        Assert.Equal(987654, observation.RuntimePeakWorkingSetBytes);
        Assert.Equal(1, host.CaptureMetricsCalls);
        Assert.True(observation.LatencyMilliseconds >= 0);
        Assert.DoesNotContain("private", System.Text.Json.JsonSerializer.Serialize(observation), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateAsync_ReportsFirstRuntimeRequestSeparatelyFromSubsequentRequests()
    {
        var observations = new List<ProviderRequestTelemetry>();
        var handler = new DelegateHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"result\"}}]}", Encoding.UTF8, "application/json")
        }));
        var host = new StaticRuntimeHost(
            new LocalRuntimeEndpoint(new Uri("http://127.0.0.1:51821"), "local-secret", "qwen-test", backend: "Vulkan"),
            new LocalRuntimeMetrics(321.5, 987654));
        using var httpClient = new HttpClient(handler);
        using var client = new LocalTextGenerationClient(host, new ProviderProfile
        {
            Platform = ProviderPlatform.ManagedLocal,
            Type = ProviderType.Local,
            Model = "qwen-test",
            LocalModelInstallationId = "qwen-test@1"
        }, httpClient, observations.Add);

        await client.GenerateAsync("system", "user");
        await client.GenerateAsync("system", "user");

        Assert.Equal(2, observations.Count);
        Assert.True(observations[0].IsFirstRequestAfterRuntimeStart);
        Assert.False(observations[1].IsFirstRequestAfterRuntimeStart);
        Assert.Null(observations[0].RuntimeBackend);
    }

    [Fact]
    public void RuntimeEndpoint_RemoteAddressIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new LocalRuntimeEndpoint(new Uri("https://models.example"), "secret", "model"));
    }

    [Fact]
    public void AdapterBinding_MustMatchBaseModelHash()
    {
        var model = new InstalledLocalModel { InstallationId = "base@1", Sha256 = "a" };
        var adapter = new InstalledLocalAdapter { InstallationId = "adapter@1", BaseModelSha256 = "b" };

        Assert.Throws<InvalidDataException>(() => LocalRuntimeManager.ValidateAdapterBinding(model, adapter));
    }

    private static string ValueAfter(ICollection<string> arguments, string key)
    {
        var values = arguments.ToList();
        var index = values.IndexOf(key);
        Assert.True(index >= 0 && index + 1 < values.Count, $"Missing argument {key}");
        return values[index + 1];
    }

    private static string CreateTestRoot(string name)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", "LocalRuntime_" + name + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private sealed class StaticRuntimeHost(LocalRuntimeEndpoint endpoint, LocalRuntimeMetrics? metrics = null) : ILocalRuntimeHost, ILocalRuntimeRequestDiagnostics
    {
        public int StopCalls { get; private set; }
        public int CaptureMetricsCalls { get; private set; }

        public Task<LocalRuntimeEndpoint> EnsureStartedAsync(ProviderProfile profile, CancellationToken cancellationToken = default) =>
            Task.FromResult(endpoint);

        public LocalRuntimeMetrics? CaptureRequestMetrics()
        {
            CaptureMetricsCalls++;
            return metrics is null ? null : metrics with { IsFirstRequestAfterStart = CaptureMetricsCalls == 1 };
        }

        public void Stop() => StopCalls++;
    }
}
