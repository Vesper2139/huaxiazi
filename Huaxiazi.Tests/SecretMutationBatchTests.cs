using System;
using System.Collections.Generic;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class SecretMutationBatchTests
{
    private sealed class MemoryStore : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = new();
        public void Save(string id, string secret) => Values[id] = secret;
        public string? Read(string id) => Values.TryGetValue(id, out var value) ? value : null;
        public bool Exists(string id) => Values.ContainsKey(id);
        public void Delete(string id) => Values.Remove(id);
    }

    [Fact]
    public void Execute_AppliesEveryProfileChangeBeforePersistingConfiguration()
    {
        var store = new MemoryStore();
        store.Values["one"] = "old-one";
        store.Values["deleted"] = "old-deleted";
        var persisted = false;

        SecretMutationBatch.Execute(store,
        [
            SecretMutation.Replace("one", "new-one"),
            SecretMutation.Replace("two", "new-two"),
            SecretMutation.Remove("deleted")
        ], () => persisted = true);

        Assert.True(persisted);
        Assert.Equal("new-one", store.Read("one"));
        Assert.Equal("new-two", store.Read("two"));
        Assert.Null(store.Read("deleted"));
    }

    [Fact]
    public void Execute_WhenConfigurationPersistenceFails_RestoresAllOriginalSecrets()
    {
        var store = new MemoryStore();
        store.Values["one"] = "old-one";
        store.Values["deleted"] = "old-deleted";

        Assert.Throws<InvalidOperationException>(() => SecretMutationBatch.Execute(store,
        [
            SecretMutation.Replace("one", "new-one"),
            SecretMutation.Replace("two", "new-two"),
            SecretMutation.Remove("deleted")
        ], () => throw new InvalidOperationException("config failed")));

        Assert.Equal("old-one", store.Read("one"));
        Assert.Null(store.Read("two"));
        Assert.Equal("old-deleted", store.Read("deleted"));
    }
}
