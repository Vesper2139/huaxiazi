using System;
using System.Collections.Concurrent;

namespace Huaxiazi.Services;

public interface IIdempotencyStore
{
    bool TryGet(string key, out AgentToolResult result);
    void Put(string key, AgentToolResult result);
}

public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset ExpiresAt, AgentToolResult Result)> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;

    public InMemoryIdempotencyStore(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? TimeSpan.FromMinutes(10);
        if (_ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));
    }

    public bool TryGet(string key, out AgentToolResult result)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt > DateTimeOffset.UtcNow) { result = entry.Result; return true; }
            _entries.TryRemove(key, out _);
        }
        result = null!;
        return false;
    }

    public void Put(string key, AgentToolResult result) => _entries[key] = (DateTimeOffset.UtcNow.Add(_ttl), result);
}
