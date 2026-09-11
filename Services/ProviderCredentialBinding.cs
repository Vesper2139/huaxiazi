using System;
using System.Security.Cryptography;
using System.Text;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

/// <summary>
/// Derives a credential slot for custom endpoints from the complete endpoint
/// identity. A copied config cannot reuse the legacy provider-default slot for
/// a different custom host without requiring the user to enter the key again.
/// </summary>
internal static class ProviderCredentialBinding
{
    internal static string ForEndpointIdentity(ProviderProfile profile, string prefix = "provider-bound")
    {
        ArgumentNullException.ThrowIfNull(profile);
        var canonical = string.Join("\n", profile.Id, profile.Type, profile.Platform,
            profile.Protocol, profile.ApiBase.Trim());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant()[..16];
        return prefix + "-" + hash;
    }

    internal static string ForCustomEndpoint(ProviderProfile profile) =>
        ForEndpointIdentity(profile, "provider-custom");

    internal static string ForProviderSwitch(ProviderProfile profile) =>
        profile.Platform == ProviderPlatform.CustomOpenAICompatible
            ? ForCustomEndpoint(profile)
            : ForEndpointIdentity(profile);

    internal static string ForProfile(ProviderProfile profile) =>
        profile.Platform == ProviderPlatform.CustomOpenAICompatible
            ? ForCustomEndpoint(profile)
            : "provider-" + profile.Id;
}
