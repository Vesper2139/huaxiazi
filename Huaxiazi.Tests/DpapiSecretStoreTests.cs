using System;
using System.IO;
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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
