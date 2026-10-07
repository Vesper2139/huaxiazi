using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class LocalModelStoreTests
{
    [Fact]
    public async Task ImportModelAsync_CopiesVerifiedGgufIntoManagedDirectoryAndPersistsRegistry()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "source.gguf");
            await File.WriteAllBytesAsync(source, GgufBytes("model-payload"));

            var imported = await new LocalModelStore(Path.Combine(root, "managed"))
                .ImportModelAsync(source, "我的模型");

            Assert.Equal(LocalModelState.Available, imported.State);
            Assert.Equal("我的模型", imported.DisplayName);
            Assert.True(imported.IsUserImported);
            Assert.StartsWith(Path.Combine(root, "managed", "imports"), imported.FilePath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(GgufBytes("model-payload"), await File.ReadAllBytesAsync(imported.FilePath));
            Assert.Equal(Sha256(GgufBytes("model-payload")), imported.Sha256);

            var reloaded = Assert.Single(new LocalModelStore(Path.Combine(root, "managed")).GetInstalledModels());
            Assert.Equal(imported.InstallationId, reloaded.InstallationId);
            Assert.Equal(imported.Sha256, reloaded.Sha256);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ImportModelAsync_NonGgufInputIsRejectedWithoutCreatingRegistryEntry()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "fake.gguf");
            await File.WriteAllTextAsync(source, "not a GGUF model");
            var store = new LocalModelStore(Path.Combine(root, "managed"));

            var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportModelAsync(source, "伪模型"));

            Assert.Contains("GGUF", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(store.GetInstalledModels());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RemoveModel_ActiveInstallationIsProtected()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "source.gguf");
            await File.WriteAllBytesAsync(source, GgufBytes("model-payload"));
            var store = new LocalModelStore(Path.Combine(root, "managed"));
            var imported = await store.ImportModelAsync(source, "活动模型");

            var error = Assert.Throws<InvalidOperationException>(() => store.RemoveModel(imported.InstallationId, imported.InstallationId));

            Assert.Contains("当前", error.Message);
            Assert.True(File.Exists(imported.FilePath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ImportAdapterAsync_BaseHashMismatchIsRejected()
    {
        var root = CreateTempDirectory();
        try
        {
            var modelSource = Path.Combine(root, "model.gguf");
            var adapterSource = Path.Combine(root, "adapter.gguf");
            var manifestPath = Path.Combine(root, "adapter.json");
            await File.WriteAllBytesAsync(modelSource, GgufBytes("base"));
            await File.WriteAllBytesAsync(adapterSource, GgufBytes("adapter"));
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new
            {
                id = "polish-lora",
                version = "1",
                displayName = "润色 LoRA",
                baseModelSha256 = new string('A', 64),
                sha256 = Sha256(GgufBytes("adapter")),
                defaultScale = 1.0
            }));
            var store = new LocalModelStore(Path.Combine(root, "managed"));
            var model = await store.ImportModelAsync(modelSource, "基础模型");

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ImportAdapterAsync(adapterSource, manifestPath, model.InstallationId));

            Assert.Contains("基础模型", error.Message);
            Assert.Empty(store.GetInstalledAdapters());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CorruptRegistry_IsReportedToDiagnosticSinkInsteadOfLookingLikeAnEmptyStore()
    {
        var root = CreateTempDirectory();
        try
        {
            var messages = new System.Collections.Generic.List<Exception>();
            var managed = Path.Combine(root, "managed");
            Directory.CreateDirectory(managed);
            File.WriteAllText(Path.Combine(managed, "registry.json"), "{ not-json }");

            var store = new LocalModelStore(managed, messages.Add);

            Assert.Empty(store.GetInstalledModels());
            Assert.Single(messages);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "HuaxiaziModels_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static byte[] GgufBytes(string payload) => Encoding.UTF8.GetBytes("GGUF" + payload);

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
