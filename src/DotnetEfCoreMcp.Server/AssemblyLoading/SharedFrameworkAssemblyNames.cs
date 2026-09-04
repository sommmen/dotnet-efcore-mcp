namespace DotnetEfCoreMcp.Server.AssemblyLoading;

/// <summary>Assembly (simple) names that MUST resolve to the copy already loaded in the default
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> rather than a second copy loaded from a
/// target project's own output folder or a compiled query's dependency closure. Without this,
/// reflection checks like <c>typeof(DbContext).IsAssignableFrom</c> would fail even for a real
/// DbContext type, because a second copy of Microsoft.EntityFrameworkCore.dll would produce a
/// *different*, type-identity-incompatible <see cref="Type"/> for <c>DbContext</c> than the one
/// our server code references. Sharing these assemblies assumes the target project's EF Core
/// major version is compatible with the server's own referenced EF Core version (both net10.0 /
/// EF Core 10.x for this MVP); a mismatched major version is out of scope and will likely surface
/// as a <see cref="System.IO.FileLoadException"/> or <see cref="MissingMethodException"/> at
/// construction time rather than being detected up front.
///
/// This list must include not only the EF Core / provider assemblies themselves but also any
/// assembly whose types appear as public members (fields, parameters, return types) on those
/// shared types, even transitively. Missing one of those is a much subtler bug than missing an
/// EF Core assembly outright: reflection over the target's own types still works, and simple
/// queries can succeed, but any code path that touches the affected member throws a confusing
/// <see cref="MissingFieldException"/>/<see cref="MissingMethodException"/>/<see cref="TypeLoadException"/>
/// deep inside EF Core, because the loading context ends up loading a second, type-identity-incompatible
/// copy of that dependency instead of sharing the default ALC's copy. For example,
/// Microsoft.Extensions.Logging.Abstractions defines <see cref="Microsoft.Extensions.Logging.EventId"/>,
/// the field type of <c>RelationalEventId.CommandExecuting</c> and friends; a target DbContext that
/// calls the extremely common <c>ConfigureWarnings(b => b.Log((RelationalEventId.CommandExecuting, ...)))</c>
/// pattern fails with "Field not found: RelationalEventId.CommandExecuting" if this assembly is
/// missing here, even though the field very much exists - the runtime just resolved the field token
/// against the wrong copy of the declaring assembly's dependency closure. Likewise
/// Microsoft.EntityFrameworkCore.SqlServer's <c>SqlServerDbFunctionsExtensions</c> exposes a public
/// <c>VectorDistance</c> overload that references <c>Microsoft.Data.SqlClient</c> types, which in turn
/// pulls in Microsoft.Identity.Client and Microsoft.IdentityModel.Abstractions (for Azure AD
/// authentication) - all four had to be added here after a live SQL Server integration test surfaced
/// the gap.
///
/// Because this "any assembly reachable from a shared assembly's public API surface" rule is easy to
/// violate silently (adding a provider or upgrading EF Core can introduce a new public member that
/// reaches an assembly not yet listed here), <c>SharedFrameworkAssemblyClosureTests</c> walks the
/// public API closure of every entry below with a <see cref="System.Reflection.MetadataLoadContext"/>
/// and fails if it finds a non-BCL assembly that is not itself in this list. Prefer fixing a failure
/// there by adding the missing assembly name rather than suppressing the test.
///
/// Shared by both <see cref="TargetAssemblyLoadContext"/> (which loads a target project's own
/// compiled output) and <c>DotnetEfCoreMcp.Server.Compilation.CompiledQueryLoadContext</c> (which
/// loads a Roslyn-compiled <c>run_query</c> expression - see
/// <c>docs/development/roslyn-user-query.md</c>) so the allowlist only needs to be maintained in
/// one place.</summary>
internal static class SharedFrameworkAssemblyNames
{
    public static readonly IReadOnlySet<string> Value = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.EntityFrameworkCore",
        "Microsoft.EntityFrameworkCore.Abstractions",
        "Microsoft.EntityFrameworkCore.Relational",
        "Microsoft.EntityFrameworkCore.Sqlite",
        "Microsoft.EntityFrameworkCore.SqlServer",
        "Microsoft.AspNetCore.Identity",
        "Microsoft.AspNetCore.Identity.EntityFrameworkCore",
        "Microsoft.Extensions.Identity.Core",
        "Microsoft.Extensions.Identity.Stores",
        "Microsoft.Extensions.Logging",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Caching.Abstractions",
        "Microsoft.Extensions.Options",
        "Microsoft.Extensions.Primitives",
        "Microsoft.Data.Sqlite",
        "Microsoft.Data.SqlClient",
        "Microsoft.Identity.Client",
        "Microsoft.IdentityModel.Abstractions",
        "SQLitePCLRaw.core",
        "SQLitePCLRaw.provider.e_sqlite3",
        "SQLitePCLRaw.batteries_v2",
        "Npgsql",
        "Npgsql.EntityFrameworkCore.PostgreSQL",
        "System.Linq.Dynamic.Core",
    };
}
