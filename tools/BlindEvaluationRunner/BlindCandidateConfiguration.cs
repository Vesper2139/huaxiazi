using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Huaxiazi.Models;
using Huaxiazi.Services;

namespace Huaxiazi.BlindEvaluationRunner;

/// <summary>
/// Candidate configuration file shape. Credentials are referenced by environment
/// variable name and never stored in this object as serialized configuration.
/// </summary>
public sealed class BlindCandidateProfileFile
{
    public required ProviderProfile Profile { get; init; }
    public string ApiKeyEnvironmentVariable { get; init; } = string.Empty;
}

/// <summary>Resolved in-memory configuration for a single explicit candidate run.</summary>
public sealed record BlindCandidateRuntimeConfiguration(
    ProviderProfile Profile,
    string ApiKey,
    string AuthorizationReference)
{
    public BlindLocalArtifactProvenance? LocalArtifacts { get; init; }
}

public static class BlindCandidateConfigurationLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static BlindCandidateRuntimeConfiguration LoadFromFile(
        string path,
        bool allowCloud,
        string authorizationReference,
        Func<string, string?> environmentLookup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Load(System.IO.File.ReadAllText(path), allowCloud, authorizationReference, environmentLookup);
    }

    public static BlindCandidateRuntimeConfiguration Load(
        string json,
        bool allowCloud,
        string authorizationReference,
        Func<string, string?> environmentLookup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(environmentLookup);

        var file = JsonSerializer.Deserialize<BlindCandidateProfileFile>(json, JsonOptions)
            ?? throw new JsonException("候选配置为空。");
        ArgumentNullException.ThrowIfNull(file.Profile);
        if (file.Profile.ModelMapping is null || file.Profile.LocalRuntimeOptions is null)
            throw new InvalidOperationException("候选 profile 的模型映射或本地运行参数为空。");
        var profile = file.Profile.Clone();
        if (string.IsNullOrWhiteSpace(profile.Model))
            throw new InvalidOperationException("候选 profile 缺少模型标识。");
        if (!Enum.IsDefined(profile.Platform) || !Enum.IsDefined(profile.Protocol) || !Enum.IsDefined(profile.Type))
            throw new InvalidOperationException("候选 profile 包含不支持的平台或协议。");
        var platform = ProviderPlatformCatalog.Get(profile.Platform);
        if (platform.Type != profile.Type || platform.Protocol != profile.Protocol)
            throw new InvalidOperationException("候选 profile 的 Provider 类型/协议与当前平台定义不一致。");
        if (profile.TimeoutSeconds is < 10 or > 600 || !double.IsFinite(profile.Temperature) ||
            profile.Temperature is < 0 or > 2 || !double.IsFinite(profile.TopP) || profile.TopP is < 0 or > 1 ||
            profile.MaxTokens is < 128 or > 32768)
            throw new InvalidOperationException("候选 profile 的超时或采样参数超出应用实际支持范围。");
        if (!Uri.TryCreate(profile.ApiBase, UriKind.Absolute, out var apiBase))
            throw new InvalidOperationException("候选 profile 的 API 地址无效。");
        if (apiBase.UserInfo.Length > 0 || apiBase.Query.Length > 0 || apiBase.Fragment.Length > 0)
            throw new InvalidOperationException("候选 API 地址不能在路径中嵌入凭据、查询参数或片段。");

        if (profile.Type == ProviderType.Cloud)
        {
            if (!allowCloud)
                throw new InvalidOperationException("云端候选必须显式附带 --allow-cloud 才能执行。");
            if (apiBase.Scheme != Uri.UriSchemeHttps || apiBase.IsLoopback)
                throw new InvalidOperationException("云端候选必须使用非 loopback HTTPS 地址。");
            if (string.IsNullOrWhiteSpace(authorizationReference))
                throw new InvalidOperationException("云端候选必须显式提供 --authorization-ref。");
            if (string.IsNullOrWhiteSpace(file.ApiKeyEnvironmentVariable) ||
                !IsEnvironmentVariableName(file.ApiKeyEnvironmentVariable))
                throw new InvalidOperationException("云端候选必须配置有效的 api_key_environment_variable 名称。");

            // Deliberately resolve secrets only after all explicit cloud gates pass.
            var apiKey = environmentLookup(file.ApiKeyEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("指定的 API key 环境变量未设置或为空。");
            return new(profile, apiKey, authorizationReference.Trim());
        }

        if (profile.Type != ProviderType.Local)
            throw new InvalidOperationException("候选 profile 的 Provider 类型无效。");
        if (profile.Platform == ProviderPlatform.ManagedLocal)
        {
            if (apiBase.Scheme != Uri.UriSchemeHttp || !apiBase.IsLoopback)
                throw new InvalidOperationException("ManagedLocal 候选只能使用本机 HTTP 占位地址；实际端口由本地运行时分配。");
            if (string.IsNullOrWhiteSpace(profile.LocalModelInstallationId) ||
                !string.Equals(profile.Model, profile.LocalModelInstallationId, StringComparison.Ordinal))
                throw new InvalidOperationException("ManagedLocal 候选必须绑定已安装模型的 installation ID。");
            if (!string.IsNullOrEmpty(file.ApiKeyEnvironmentVariable))
                throw new InvalidOperationException("ManagedLocal 候选不接受外部 API Key 环境变量。");
            return new(profile, string.Empty, string.Empty);
        }
        if (apiBase.Scheme != Uri.UriSchemeHttp || !apiBase.IsLoopback || apiBase.Port <= 0)
            throw new InvalidOperationException("本地候选只能使用带有效端口的 HTTP loopback 地址。");
        if (profile.Platform is not (ProviderPlatform.Ollama or ProviderPlatform.LmStudio))
            throw new InvalidOperationException("当前本地候选只支持 Ollama 或 LM Studio；自定义兼容端点尚未接入本地 Provider 安全策略。");
        return new(profile, string.Empty, string.Empty);
    }

    private static bool IsEnvironmentVariableName(string value)
    {
        if (value.Length == 0 || !(char.IsAsciiLetter(value[0]) || value[0] == '_')) return false;
        foreach (var character in value)
            if (!(char.IsAsciiLetterOrDigit(character) || character == '_')) return false;
        return true;
    }
}
