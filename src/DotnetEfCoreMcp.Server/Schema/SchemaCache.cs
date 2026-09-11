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
/// - no explicit invalidation step is required.</summary>
public sealed class SchemaCache
{
    private readonly ConditionalWeakTable<Type, ConcurrentDictionary<string, SchemaDto>> _cache = new();

    public SchemaDto GetOrBuild(Type contextType, string connectionName, Func<SchemaDto> factory)
    {
        var perConnection = _cache.GetOrCreateValue(contextType);
        return perConnection.GetOrAdd(connectionName, _ => factory());
    }

    /// <summary>Cache-only lookup: returns the already-built <see cref="SchemaDto"/> for
    /// <paramref name="contextType"/> and <paramref name="connectionName"/> without ever building
    /// one.</summary>
    public bool TryGet(Type contextType, string connectionName, out SchemaDto? schema)
    {
        if (_cache.TryGetValue(contextType, out var perConnection) &&
            perConnection.TryGetValue(connectionName, out var found))
        {
            schema = found;
            return true;
        }

        schema = null;
        return false;
    }
}
