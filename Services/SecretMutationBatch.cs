using System;
using System.Collections.Generic;
using System.Linq;

namespace Huaxiazi.Services;

public enum SecretMutationKind
{
    Replace,
    Remove
}

public sealed record SecretMutation(string SecretId, SecretMutationKind Kind, string? Value)
{
    public static SecretMutation Replace(string secretId, string value) =>
        new(secretId, SecretMutationKind.Replace, value);

    public static SecretMutation Remove(string secretId) =>
        new(secretId, SecretMutationKind.Remove, null);
}

public static class SecretMutationBatch
{
    public static void Execute(ISecretStore store, IEnumerable<SecretMutation> mutations, Action persistConfiguration)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(persistConfiguration);

        var changes = mutations.GroupBy(change => change.SecretId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToList();
        var originals = changes.ToDictionary(
            change => change.SecretId,
            change => store.Read(change.SecretId),
            StringComparer.Ordinal);
        try
        {
            foreach (var change in changes)
            {
                if (change.Kind == SecretMutationKind.Remove)
                    store.Delete(change.SecretId);
                else
                    store.Save(change.SecretId, change.Value ?? string.Empty);
            }
            persistConfiguration();
        }
        catch
        {
            foreach (var original in originals)
            {
                try
                {
                    if (original.Value is null) store.Delete(original.Key);
                    else store.Save(original.Key, original.Value);
                }
                catch
                {
                    // Best-effort compensation; preserve the original failure.
                }
            }
            throw;
        }
    }
}
