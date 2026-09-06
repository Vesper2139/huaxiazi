using System;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

/// <summary>Single fail-closed endpoint policy shared by generation, connection tests, and health checks.</summary>
internal static class ProviderEndpointPolicy
{
    internal static bool TryValidate(ProviderProfile profile, string? apiKey, out Uri? baseUri, out string error)
    {
        baseUri = null;
        error = string.Empty;
        if (profile.Type == ProviderType.Cloud && string.IsNullOrWhiteSpace(apiKey))
            return Fail("API Key 未配置。请到“设置 → 模型配置”中填写密钥。", out error);
        if (!Uri.TryCreate(profile.ApiBase, UriKind.Absolute, out baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            return Fail("API 地址无效，请填写完整的 http 或 https 地址。", out error);
        if (profile.Type == ProviderType.Cloud && baseUri.Scheme != Uri.UriSchemeHttps)
            return Fail("云端模型必须使用 HTTPS 地址，已阻止通过明文 HTTP 发送 API Key。", out error);
        if (baseUri.Scheme == Uri.UriSchemeHttp && !baseUri.IsLoopback)
            return Fail("远程模型接口必须使用 HTTPS，已阻止通过明文 HTTP 发送内容或凭据。", out error);
        if (baseUri.Scheme == Uri.UriSchemeHttp && !string.IsNullOrWhiteSpace(apiKey))
            return Fail("明文 HTTP 接口禁止携带 API Key；请移除本地密钥或改用 HTTPS。", out error);
        if (!ValidatePlatform(profile, baseUri, out error)) return false;
        if (string.IsNullOrWhiteSpace(profile.Model))
            return Fail("模型名称不能为空。", out error);
        return true;
    }

    private static bool ValidatePlatform(ProviderProfile profile, Uri baseUri, out string error)
    {
        var option = ProviderPlatformCatalog.Get(profile.Platform);
        if (profile.Platform == ProviderPlatform.CustomOpenAICompatible)
        {
            if (profile.Type != ProviderType.Cloud || profile.Protocol != ProviderProtocol.OpenAICompatible)
                return Fail("自定义接口仅允许云端 HTTPS 的 OpenAI 兼容协议。", out error);
            error = string.Empty;
            return true;
        }

        if (profile.Type != option.Type || profile.Protocol != option.Protocol)
            return Fail("模型平台类型或协议与官方预设不一致，请重新选择平台。", out error);
        if (!Uri.TryCreate(option.ApiBase, UriKind.Absolute, out var trustedBase))
            return Fail("官方模型平台配置无效。", out error);

        var sameAuthority = string.Equals(baseUri.Scheme, trustedBase.Scheme, StringComparison.OrdinalIgnoreCase) &&
                            baseUri.Port == trustedBase.Port &&
                            (option.Type == ProviderType.Local
                                ? baseUri.IsLoopback
                                : string.Equals(baseUri.IdnHost, trustedBase.IdnHost, StringComparison.OrdinalIgnoreCase));
        var configuredPath = baseUri.AbsolutePath.TrimEnd('/');
        var trustedPath = trustedBase.AbsolutePath.TrimEnd('/');
        var legacyDeepSeekRoot = profile.Platform == ProviderPlatform.DeepSeek && string.IsNullOrEmpty(configuredPath);
        if (!sameAuthority || baseUri.UserInfo.Length > 0 || !string.IsNullOrEmpty(baseUri.Query) ||
            (!string.Equals(configuredPath, trustedPath, StringComparison.OrdinalIgnoreCase) && !legacyDeepSeekRoot))
            return Fail("官方模型平台只能连接其官方 API 地址；已阻止被篡改的端点。", out error);
        error = string.Empty;
        return true;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}
