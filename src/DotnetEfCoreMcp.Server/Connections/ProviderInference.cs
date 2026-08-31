using System.Reflection;

namespace DotnetEfCoreMcp.Server.Connections;

/// <summary>Infers a <see cref="DatabaseProvider"/> from the EF Core provider package an assembly
/// references, for connections whose <see cref="ConnectionRegistryEntry.Provider"/> was left
/// unconfigured. Inference only ever inspects compiled assembly reference metadata
/// (<see cref="Assembly.GetReferencedAssemblies"/>) - never the target project's own configuration,
/// appsettings, or connection strings, which stay exclusively server-side.</summary>
public static class ProviderInference
{
    /// <summary>Maps the simple assembly name of a supported EF Core provider package to the
    /// <see cref="DatabaseProvider"/> it implements.</summary>
    private static readonly IReadOnlyDictionary<string, DatabaseProvider> KnownProviderAssemblies =
        new Dictionary<string, DatabaseProvider>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.EntityFrameworkCore.Sqlite"] = DatabaseProvider.Sqlite,
            ["Microsoft.EntityFrameworkCore.SqlServer"] = DatabaseProvider.SqlServer,
            ["Npgsql.EntityFrameworkCore.PostgreSQL"] = DatabaseProvider.PostgreSql,
        };

    /// <summary>Attempts to infer the single database provider referenced by <paramref name="assembly"/>.
    /// Returns <see langword="false"/>, with a human-readable <paramref name="error"/> explaining the
    /// problem, when zero or more than one supported provider package is referenced - in either case
    /// the operator must configure the provider explicitly via
    /// <c>Connections:&lt;name&gt;:Provider</c>.</summary>
    public static bool TryInfer(Assembly assembly, out DatabaseProvider provider, out string? error)
    {
        var referencedNames = assembly.GetReferencedAssemblies().Select(a => a.Name).Where(n => n is not null).Select(n => n!);
        return TryInfer(referencedNames, out provider, out error);
    }

    /// <summary>Same as <see cref="TryInfer(Assembly, out DatabaseProvider, out string?)"/> but takes
    /// the referenced assembly (simple) names directly, for testability without a real assembly.</summary>
    public static bool TryInfer(IEnumerable<string> referencedAssemblyNames, out DatabaseProvider provider, out string? error)
    {
        var matches = referencedAssemblyNames
            .Where(KnownProviderAssemblies.ContainsKey)
            .Select(n => KnownProviderAssemblies[n])
            .Distinct()
            .ToList();

        switch (matches.Count)
        {
            case 1:
                provider = matches[0];
                error = null;
                return true;

            case 0:
                provider = default;
                error = "Could not infer a database provider: the loaded target assembly does not reference any supported EF Core provider package " +
                    $"({string.Join(", ", KnownProviderAssemblies.Keys)}). Configure it explicitly with 'Connections:<name>:Provider'.";
                return false;

            default:
                provider = default;
                error = "Could not infer a database provider: the loaded target assembly references multiple supported EF Core provider packages " +
                    $"({string.Join(", ", matches)}). Configure it explicitly with 'Connections:<name>:Provider'.";
                return false;
        }
    }
}
