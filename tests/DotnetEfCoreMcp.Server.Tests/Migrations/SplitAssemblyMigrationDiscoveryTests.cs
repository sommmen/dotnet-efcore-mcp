using System.Text.Json;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.Migrations;
using DotnetEfCoreMcp.Server.Mutations;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Schema;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using DotnetEfCoreMcp.Server.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace DotnetEfCoreMcp.Server.Tests.Migrations;

/// <summary>Covers <c>list_migrations</c>/<c>generate_migration_script</c> when a DbContext's
/// migrations live in a separate assembly from the DbContext type itself (P1 #11a), e.g. an
/// <c>AuthDbContext</c> declared in a DAL assembly with its migrations compiled into a separate API
/// assembly. The loaded target is always <c>SplitContextApp.SplitDbContext</c> (which has no
/// migrations of its own); the optional <c>migrationsAssembly</c> tool parameter points at the
/// companion <c>SplitMigrationsApp</c> assembly.</summary>
public sealed class SplitAssemblyMigrationDiscoveryTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private EfCoreMcpTools CreateTools(
        MigrationsOptions? migrationsOptions = null,
        AssemblyLoaderOptions? assemblyLoaderOptions = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Connections:MigrationsTests:ConnectionString"] = _db.ConnectionString,
                ["Connections:MigrationsTests:Provider"] = "Sqlite",
                ["Connections:MigrationsTests:AccessMode"] = ConnectionAccessMode.ReadWrite.ToString(),
                ["Connections:MigrationsTests:Environment"] = EnvironmentType.Development.ToString(),
            })
            .Build();
        var rawSqlOptions = new RawSqlExecutionOptions();
        var effectiveMigrationsOptions = migrationsOptions ?? new MigrationsOptions { Enabled = true };

        var tools = new EfCoreMcpTools(
            new AssemblyLoaderService(assemblyLoaderOptions ?? new AssemblyLoaderOptions()),
            new AssemblyDiscoveryService(),
            new ConnectionRegistry(configuration),
            new SchemaCache(),
            new QueryExecutor(new QueryExecutionOptions(), NullLogger<QueryExecutor>.Instance),
            new RoslynQueryExecutor(new QueryExecutionOptions(), new QueryCompiler(new QueryCompilationOptions())),
            new OutOfProcessRoslynQueryExecutor(new QueryExecutionOptions()),
            new QueryExecutionOptions(),
            rawSqlOptions,
            new SqlQueryExecutor(rawSqlOptions, NullLogger<SqlQueryExecutor>.Instance),
            effectiveMigrationsOptions,
            new MigrationInspector(effectiveMigrationsOptions, NullLogger<MigrationInspector>.Instance),
            new JsonToolResultFormatter(),
            new ToolDiagnosticsOptions(),
            NullLogger<EfCoreMcpTools>.Instance,
            new EntityMutationsOptions(),
            new EntityMutationExecutor(NullLogger<EntityMutationExecutor>.Instance));
        tools.LoadAssembly(FixturePaths.SplitContextAppDllPath);
        return tools;
    }

    [Fact]
    public async Task ListMigrations_WithMigrationsAssemblyByPath_DiscoversMigrationFromSeparateAssembly()
    {
        var tools = CreateTools();

        var json = await tools.ListMigrations("SplitDbContext", "MigrationsTests", FixturePaths.SplitMigrationsAppDllPath);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("SplitDbContext", root.GetProperty("contextName").GetString());
        Assert.False(root.GetProperty("databaseExists").GetBoolean());
        Assert.Equal(0, root.GetProperty("appliedMigrations").GetArrayLength());
        Assert.Equal(1, root.GetProperty("pendingMigrations").GetArrayLength());
        Assert.Contains("InitialCreate", root.GetProperty("pendingMigrations")[0].GetProperty("MigrationId").GetString());
    }

    [Fact]
    public void ResolveMigrationsAssembly_BySimpleName_ResolvesAsProjectReferenceDependency()
    {
        // SplitContextApp has no dependency on SplitMigrationsApp (it's the other way around:
        // SplitMigrationsApp references SplitContextApp), so by-name resolution can't be exercised
        // from that direction at the tool level - name-based resolution only succeeds when the
        // migrations assembly is a genuine dependency of the *loaded target*. This test loads
        // SplitMigrationsApp as the target directly against AssemblyLoaderService (bypassing
        // EfCoreMcpTools' context-type discovery, which is out of scope here) to prove the by-name
        // resolution path itself works via AssemblyDependencyResolver against SplitMigrationsApp's
        // own .deps.json, which does list SplitContextApp as a project reference.
        var assemblyLoader = new AssemblyLoaderService();
        var handle = assemblyLoader.Load(FixturePaths.SplitMigrationsAppDllPath);

        var resolved = assemblyLoader.ResolveMigrationsAssembly(handle, "SplitContextApp");

        Assert.Equal("SplitContextApp", resolved.GetName().Name);
    }

    [Fact]
    public async Task ListMigrations_WithoutMigrationsAssembly_FindsNoMigrationsInContextOnlyAssembly()
    {
        var tools = CreateTools();

        var json = await tools.ListMigrations("SplitDbContext", "MigrationsTests");

        using var document = JsonDocument.Parse(json);
        Assert.Equal(0, document.RootElement.GetProperty("pendingMigrations").GetArrayLength());
    }

    [Fact]
    public async Task ListMigrations_WithMigrationsAssemblyPathOutsideAllowedRoots_ThrowsRedactedError()
    {
        // Restrict AllowedRoots to only the context assembly's own directory (so CreateTools'
        // LoadAssembly call still succeeds) - the sibling SplitMigrationsApp directory then falls
        // outside the allowed roots, exercising ResolveMigrationsAssembly's own containment check.
        var contextAppRoot = Path.GetDirectoryName(FixturePaths.SplitContextAppDllPath)!;
        var tools = CreateTools(assemblyLoaderOptions: new AssemblyLoaderOptions { AllowedRoots = [contextAppRoot] });

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            tools.ListMigrations("SplitDbContext", "MigrationsTests", FixturePaths.SplitMigrationsAppDllPath));

        Assert.Contains("outside the configured allowed roots", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveMigrationsAssembly_WithPathOutsideTargetsNarrowedAllowedRoots_ThrowsEvenWhenServerWideRootAllowsIt()
    {
        // The target is registered with a narrower AssemblyTargetOptions.AllowedRoots than the
        // server-wide AssemblyLoader:AllowedRoots - proves ResolveMigrationsAssembly enforces the
        // per-target narrowing carried on the handle, not just the server-wide list (a target
        // scoped to one tenant/root must not be able to pull a migrations assembly from another
        // server-wide-allowed root).
        var contextAppRoot = Path.GetDirectoryName(FixturePaths.SplitContextAppDllPath)!;
        var migrationsAppRoot = Path.GetDirectoryName(FixturePaths.SplitMigrationsAppDllPath)!;
        var assemblyLoader = new AssemblyLoaderService(new AssemblyLoaderOptions
        {
            AllowedRoots = [contextAppRoot, migrationsAppRoot],
        });
        var handle = assemblyLoader.Load(
            FixturePaths.SplitContextAppDllPath,
            "narrowed",
            new AssemblyTargetOptions { Path = FixturePaths.SplitContextAppDllPath, AllowedRoots = [contextAppRoot] });

        var exception = Assert.Throws<AssemblyLoadFailedException>(() =>
            assemblyLoader.ResolveMigrationsAssembly(handle, FixturePaths.SplitMigrationsAppDllPath));

        Assert.Contains("outside this target's configured allowed roots", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveMigrationsAssembly_WithPathInsideTargetsNarrowedAllowedRoots_Succeeds()
    {
        var contextAppRoot = Path.GetDirectoryName(FixturePaths.SplitContextAppDllPath)!;
        var migrationsAppRoot = Path.GetDirectoryName(FixturePaths.SplitMigrationsAppDllPath)!;
        var assemblyLoader = new AssemblyLoaderService(new AssemblyLoaderOptions
        {
            AllowedRoots = [contextAppRoot, migrationsAppRoot],
        });
        var handle = assemblyLoader.Load(
            FixturePaths.SplitContextAppDllPath,
            "narrowed",
            new AssemblyTargetOptions
            {
                Path = FixturePaths.SplitContextAppDllPath,
                AllowedRoots = [contextAppRoot, migrationsAppRoot],
            });

        var resolved = assemblyLoader.ResolveMigrationsAssembly(handle, FixturePaths.SplitMigrationsAppDllPath);

        Assert.Equal("SplitMigrationsApp", resolved.GetName().Name);
    }

    [Fact]
    public async Task ListMigrations_WithUnresolvableMigrationsAssemblyName_ThrowsRedactedError()
    {
        var tools = CreateTools();

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            tools.ListMigrations("SplitDbContext", "MigrationsTests", "NotARealAssembly"));

        Assert.Contains("could not be resolved", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListMigrations_WithMissingMigrationsAssemblyPath_ThrowsRedactedError()
    {
        var tools = CreateTools();
        var missingPath = Path.Combine(Path.GetDirectoryName(FixturePaths.SplitMigrationsAppDllPath)!, "DoesNotExist.dll");

        var exception = await Assert.ThrowsAsync<McpException>(() =>
            tools.ListMigrations("SplitDbContext", "MigrationsTests", missingPath));

        Assert.Contains("not found", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateMigrationScript_WithMigrationsAssembly_ReturnsScriptWithoutMutatingDatabase()
    {
        var tools = CreateTools();

        var json = await tools.GenerateMigrationScript(
            "SplitDbContext",
            "MigrationsTests",
            idempotent: false,
            migrationsAssembly: FixturePaths.SplitMigrationsAppDllPath);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("SplitDbContext", root.GetProperty("contextName").GetString());
        Assert.Contains("CREATE TABLE", root.GetProperty("sql").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Widgets", root.GetProperty("sql").GetString(), StringComparison.Ordinal);

        var dbFilePath = _db.ConnectionString["Data Source=".Length..];
        Assert.False(File.Exists(dbFilePath));
    }
}
