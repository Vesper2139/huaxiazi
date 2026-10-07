using Huaxiazi.BlindEvaluationRunner;
using Huaxiazi.Models;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class BlindCandidateConfigurationTests
{
    [Fact]
    public void Load_CloudCandidateRequiresExplicitAllowAndDoesNotReadSecretBeforeConsent()
    {
        var json = """
            {
              "profile": {
                "id": "candidate-cloud",
                "name": "cloud candidate",
                "type": "Cloud",
                "platform": "OpenAI",
                "protocol": "OpenAICompatible",
                "api_base": "https://api.example.test/v1",
                "model": "model-v1"
              },
              "api_key_environment_variable": "HXZ_EVAL_API_KEY"
            }
            """;
        var envRead = false;

        var ex = Assert.Throws<InvalidOperationException>(() => BlindCandidateConfigurationLoader.Load(
            json, allowCloud: false, authorizationReference: "consent-2026-001", _ => { envRead = true; return "secret"; }));

        Assert.Contains("--allow-cloud", ex.Message, StringComparison.Ordinal);
        Assert.False(envRead);
    }

    [Fact]
    public void Load_CloudCandidateRequiresEnvironmentSecretAndNeverReadsItBeforeConsent()
    {
        var json = """
            {
              "profile": {
                "id": "candidate-cloud",
                "name": "cloud candidate",
                "type": "Cloud",
                "platform": "OpenAI",
                "protocol": "OpenAICompatible",
                "api_base": "https://api.example.test/v1",
                "model": "model-v1"
              },
              "api_key_environment_variable": "HXZ_EVAL_API_KEY"
            }
            """;

        var loaded = BlindCandidateConfigurationLoader.Load(json, allowCloud: true,
            authorizationReference: "consent-2026-001", _ => "secret-value");

        Assert.Equal("model-v1", loaded.Profile.Model);
        Assert.Equal("secret-value", loaded.ApiKey);
        Assert.DoesNotContain("secret-value", System.Text.Json.JsonSerializer.Serialize(loaded.Profile), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_LocalCandidateRejectsNonLoopbackAndAllowsLoopbackWithoutApiKey()
    {
        var remote = LocalProfile("http://192.0.2.40:11434/v1");
        Assert.Throws<InvalidOperationException>(() => BlindCandidateConfigurationLoader.Load(remote, false, "", _ => null));

        var local = BlindCandidateConfigurationLoader.Load(LocalProfile("http://localhost:11434/v1"), false, "", _ => null);

        Assert.Equal("qwen3", local.Profile.Model);
        Assert.Equal(string.Empty, local.ApiKey);
    }

    [Fact]
    public void Load_LocalCandidateRejectsCustomPlatformNotSupportedByProductionEndpointPolicy()
    {
        var unsupported = LocalProfile("http://127.0.0.1:8000/v1").Replace(
            "\"platform\": \"Ollama\"", "\"platform\": \"CustomOpenAICompatible\"", StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() =>
            BlindCandidateConfigurationLoader.Load(unsupported, false, "", _ => null));
    }

    [Fact]
    public void Load_ManagedLocalCandidateRequiresInstalledModelReferenceAndDoesNotRequireApiKey()
    {
        var json = ManagedLocalProfile("installed-model-123");

        var loaded = BlindCandidateConfigurationLoader.Load(json, false, "", _ => null);

        Assert.Equal(ProviderPlatform.ManagedLocal, loaded.Profile.Platform);
        Assert.Equal("installed-model-123", loaded.Profile.LocalModelInstallationId);
        Assert.Equal(string.Empty, loaded.ApiKey);
    }

    [Fact]
    public void Load_ManagedLocalCandidateRejectsMissingInstalledModelReference()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            BlindCandidateConfigurationLoader.Load(ManagedLocalProfile(""), false, "", _ => null));

        Assert.Contains("安装", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RejectsProviderProtocolMismatchAndOutOfRangeSamplingInsteadOfSilentlyClamping()
    {
        var protocolMismatch = LocalProfile("http://127.0.0.1:11434/v1").Replace(
            "\"protocol\": \"OpenAICompatible\"", "\"protocol\": \"AnthropicMessages\"", StringComparison.Ordinal);
        var invalidTemperature = LocalProfile("http://127.0.0.1:11434/v1").Replace(
            "\"model\": \"qwen3\"", "\"model\": \"qwen3\", \"temperature\": 3.0", StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => BlindCandidateConfigurationLoader.Load(protocolMismatch, false, "", _ => null));
        Assert.Throws<InvalidOperationException>(() => BlindCandidateConfigurationLoader.Load(invalidTemperature, false, "", _ => null));
    }

    [Fact]
    public void Load_CloudCandidateRequiresSeparateAuthorizationReference()
    {
        var json = """
            {
              "profile": {
                "id": "candidate-cloud",
                "name": "cloud candidate",
                "type": "Cloud",
                "platform": "OpenAI",
                "protocol": "OpenAICompatible",
                "api_base": "https://api.example.test/v1",
                "model": "model-v1"
              },
              "api_key_environment_variable": "HXZ_EVAL_API_KEY"
            }
            """;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            BlindCandidateConfigurationLoader.Load(json, true, "", _ => "secret-value"));

        Assert.Contains("--authorization-ref", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_CloudCandidateRejectsCredentialBearingUrlBeforeReadingEnvironmentSecret()
    {
        var json = """
            {
              "profile": {
                "id": "candidate-cloud",
                "name": "cloud candidate",
                "type": "Cloud",
                "platform": "OpenAI",
                "protocol": "OpenAICompatible",
                "api_base": "https://api.example.test/v1?key=embedded",
                "model": "model-v1"
              },
              "api_key_environment_variable": "HXZ_EVAL_API_KEY"
            }
            """;
        var envRead = false;

        Assert.Throws<InvalidOperationException>(() => BlindCandidateConfigurationLoader.Load(
            json, true, "consent-2026-001", _ => { envRead = true; return "secret-value"; }));

        Assert.False(envRead);
    }

    [Fact]
    public void Load_RejectsInlineCredentialFieldsInsteadOfSilentlyIgnoringThem()
    {
        var json = LocalProfile("http://127.0.0.1:11434/v1").Replace(
            "\"api_key_environment_variable\": \"\"",
            "\"api_key\": \"inline-secret\", \"api_key_environment_variable\": \"\"",
            StringComparison.Ordinal);

        Assert.Throws<System.Text.Json.JsonException>(() =>
            BlindCandidateConfigurationLoader.Load(json, false, "", _ => null));
    }

    private static string LocalProfile(string apiBase) => $$"""
        {
          "profile": {
            "id": "local-1",
            "name": "local candidate",
            "type": "Local",
            "platform": "Ollama",
            "protocol": "OpenAICompatible",
            "api_base": "{{apiBase}}",
            "model": "qwen3"
          },
          "api_key_environment_variable": ""
        }
        """;

    private static string ManagedLocalProfile(string installationId) => $$"""
        {
          "profile": {
            "id": "managed-local-1",
            "name": "managed local candidate",
            "type": "Local",
            "platform": "ManagedLocal",
            "protocol": "OpenAICompatible",
            "api_base": "http://127.0.0.1:0/v1",
            "model": "{{(string.IsNullOrWhiteSpace(installationId) ? "managed-model" : installationId)}}",
            "local_model_installation_id": "{{installationId}}"
          },
          "api_key_environment_variable": ""
        }
        """;
}
