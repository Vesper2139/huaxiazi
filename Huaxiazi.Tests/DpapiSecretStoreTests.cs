using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class DpapiSecretStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HuaxiaziSecrets_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveAndRead_EncryptsSecretAtRestForCurrentWindowsUser()
    {
        var store = new DpapiSecretStore(_root);

        store.Save("profile-default", "sk-sensitive-value");

        Assert.Equal("sk-sensitive-value", store.Read("profile-default"));
        var bytes = File.ReadAllBytes(Assert.Single(Directory.GetFiles(_root)));
        Assert.DoesNotContain("sk-sensitive-value", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Delete_RemovesStoredSecret()
    {
        var store = new DpapiSecretStore(_root);
        store.Save("profile-default", "secret");

        store.Delete("profile-default");

        Assert.Null(store.Read("profile-default"));
    }

    [Fact]
    public void Save_SecretDirectoryDoesNotInheritBroadReadAccess()
    {
        var store = new DpapiSecretStore(_root);

        store.Save("profile-default", "secret");

        var security = new DirectoryInfo(_root).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        Assert.DoesNotContain(rules, rule =>
            rule.AccessControlType == AccessControlType.Allow &&
            rule.IdentityReference is SecurityIdentifier sid &&
            (sid.IsWellKnown(WellKnownSidType.WorldSid) ||
             sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid) ||
             sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
