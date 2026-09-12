using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace DotnetEfCoreMcp.Server.Schema;

/// <summary>Caches a built <see cref="SchemaDto"/> per DbContext CLR <see cref="Type"/> and
/// registered connection name. Two connections can share a DbContext type while pointing at
/// different providers/databases, and the built schema captures provider-specific relational
/// metadata (e.g. column types), so the connection name is part of the cache key to avoid one
/// connection's schema being reused for another. The outer map is a
/// <see cref="ConditionalWeakTable{TKey,TValue}"/> keyed on the <see cref="Type"/> object itself:
/// when a target assembly is reloaded, its old <see cref="Type"/> instances become unreachable
/// once the previous <see cref="System.Runtime.Loader.AssemblyLoadContext"/> is unloaded and
/// collected, which naturally drops all of their cached schema entries (for every connection) too
/// - no explicit invalidation step is required. Each per-connection entry is a
/// <see cref="Lazy{T}"/> using <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> so that,
/// unlike a plain <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey,Func{TKey,TValue})"/>
/// factory (which may run more than once under a race), concurrent callers for the same
/// (type, connection) block on one another and the build runs exactly once.</summary>
public sealed class SchemaCache
{
    private readonly ConditionalWeakTable<Type, ConcurrentDictionary<string, Lazy<SchemaDto>>> _cache = new();

    public SchemaDto GetOrBuild(Type contextType, string connectionName, Func<SchemaDto> factory)
    {
        var perConnection = _cache.GetOrCreateValue(contextType);
        var lazy = perConnection.GetOrAdd(
            connectionName,
            _ => new Lazy<SchemaDto>(factory, LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    /// <summary>Cache-only lookup: returns the already-built <see cref="SchemaDto"/> for
    /// <paramref name="contextType"/> and <paramref name="connectionName"/> without ever building
    /// one.</summary>
    public bool TryGet(Type contextType, string connectionName, out SchemaDto? schema)
    {
        if (_cache.TryGetValue(contextType, out var perConnection) &&
            perConnection.TryGetValue(connectionName, out var found) &&
            found.IsValueCreated)
        {
            schema = found.Value;
            return true;
        }

        schema = null;
        return false;
    }
}
