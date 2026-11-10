using DotnetEfCoreMcp.Server.Connections;

namespace DotnetEfCoreMcp.Server.Compilation;

/// <summary>Partitions the provider-specific entries of
/// <see cref="AssemblyLoading.SharedFrameworkAssemblyNames"/> into one group per
/// <see cref="DatabaseProvider"/>, so <see cref="QueryCompiler"/> can decide per group - rather
/// than per assembly - whether a provider's surface takes part in a query compilation.
///
/// <para>Grouping exists because a provider surface is only safe to reference <em>entire</em>. An
/// EF Core provider package's public extension methods take parameter types from its underlying
/// ADO.NET/driver assembly, so referencing <c>Npgsql.EntityFrameworkCore.PostgreSQL</c> without
/// <c>Npgsql</c> puts methods into overload resolution whose signatures the compiler cannot bind:
/// an ordinary SQL Server query calling <c>.Contains(...)</c> then fails with CS0012 naming
/// <c>NpgsqlTsQuery</c> and demanding a reference to an assembly the caller never mentioned
/// (<see href="https://github.com/sommmen/dotnet-efcore-mcp/issues/122">#122</see>). That half-
/// referenced state used to depend on which assemblies the server had happened to load by the
/// time a query arrived, which is why the same query text could succeed and then fail within one
/// session.</para>
///
/// <para>A group is included when it matches the connection's provider, or when the target's own
/// dependency closure reaches it (a context may reference a provider it is not currently
/// connected with, e.g. SQLite for tests alongside SQL Server for production). Everything else is
/// excluded outright: a SQL Server connection has no reason to bring PostgreSQL extension methods
/// into overload resolution at all.</para></summary>
internal static class ProviderReferenceGroups
{
    private static readonly IReadOnlyDictionary<DatabaseProvider, string[]> Groups =
        new Dictionary<DatabaseProvider, string[]>
        {
            [DatabaseProvider.Sqlite] =
            [
                "Microsoft.EntityFrameworkCore.Sqlite",
                "Microsoft.Data.Sqlite",
                "SQLitePCLRaw.core",
                "SQLitePCLRaw.provider.e_sqlite3",
                "SQLitePCLRaw.batteries_v2",
            ],
            [DatabaseProvider.SqlServer] =
            [
                "Microsoft.EntityFrameworkCore.SqlServer",
                "Microsoft.Data.SqlClient",
                "Microsoft.Identity.Client",
                "Microsoft.IdentityModel.Abstractions",
            ],
            [DatabaseProvider.PostgreSql] =
            [
                "Npgsql.EntityFrameworkCore.PostgreSQL",
                "Npgsql",
            ],
        };

    /// <summary>Every provider-specific shared assembly name, across all providers.</summary>
    public static readonly IReadOnlySet<string> AllNames =
        new HashSet<string>(Groups.Values.SelectMany(names => names), StringComparer.OrdinalIgnoreCase);

    /// <summary>Splits the provider-specific shared assembly names into the ones this compilation
    /// should reference and the ones it must leave out entirely.</summary>
    /// <param name="provider">The provider of the connection the query runs against, or
    /// <see langword="null"/> when it is not known - in which case every group is included, since
    /// a complete reference set is still strictly better than a half-referenced one.</param>
    /// <param name="targetReferences">Answers whether the target assembly's own dependency closure
    /// reaches the named assembly.</param>
    public static ProviderReferenceScope Resolve(DatabaseProvider? provider, Func<string, bool> targetReferences)
    {
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (groupProvider, names) in Groups)
        {
            var include = provider is null || provider == groupProvider || names.Any(targetReferences);
            foreach (var name in names)
            {
                (include ? included : excluded).Add(name);
            }
        }

        // A name shared by two groups (none today, but the grouping is data) must stay referenced
        // if any group that claims it is included, or the included group would be half-referenced.
        excluded.ExceptWith(included);
        return new ProviderReferenceScope(included, excluded);
    }

    /// <summary>Names belonging to an included group that could not be resolved in full, and which
    /// must therefore be dropped rather than referenced in part.</summary>
    /// <param name="included">The included names, from <see cref="Resolve"/>.</param>
    /// <param name="resolved">The names a path was actually found for.</param>
    public static IEnumerable<string> UnsatisfiedGroupNames(IReadOnlySet<string> included, IEnumerable<string> resolved)
    {
        var found = new HashSet<string>(resolved, StringComparer.OrdinalIgnoreCase);
        foreach (var names in Groups.Values)
        {
            var groupNames = names.Where(included.Contains).ToArray();
            if (groupNames.Length == 0 || groupNames.All(found.Contains)) continue;

            foreach (var name in groupNames) yield return name;
        }
    }
}

/// <summary>The outcome of <see cref="ProviderReferenceGroups.Resolve"/>: provider-specific shared
/// assembly names a query compilation must reference, and the ones it must not.</summary>
/// <param name="Included">Names to reference, whether or not the server has already loaded them.</param>
/// <param name="Excluded">Names to leave out even when the server has loaded them.</param>
internal sealed record ProviderReferenceScope(IReadOnlySet<string> Included, IReadOnlySet<string> Excluded);
