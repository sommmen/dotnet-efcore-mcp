using System.Runtime.CompilerServices;

namespace DotnetEfCoreMcp.Server.Schema;

/// <summary>Caches a built <see cref="SchemaDto"/> per DbContext CLR <see cref="Type"/>. Uses a
/// <see cref="ConditionalWeakTable{TKey,TValue}"/> keyed on the <see cref="Type"/> object itself:
/// when a target assembly is reloaded, its old <see cref="Type"/> instances become unreachable
/// once the previous <see cref="System.Runtime.Loader.AssemblyLoadContext"/> is unloaded and
/// collected, which naturally drops their cached schema entries too - no explicit invalidation
/// step is required.</summary>
public sealed class SchemaCache
{
    private readonly ConditionalWeakTable<Type, SchemaDto> _cache = new();

    public SchemaDto GetOrBuild(Type contextType, Func<SchemaDto> factory)
    {
        return _cache.GetValue(contextType, _ => factory());
    }
}
