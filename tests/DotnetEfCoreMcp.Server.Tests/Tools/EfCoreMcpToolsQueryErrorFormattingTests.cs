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

namespace DotnetEfCoreMcp.Server.Tests.Tools;

/// <summary>Covers <c>run_query</c>'s structured error formatting (<c>FormatQueryError</c>) for
/// Roslyn compile/configuration failures - complementing the existing Dynamic LINQ-focused
/// coverage in <see cref="EfCoreMcpToolsSchemaSelectionTests"/> - to ensure the caller gets an
/// actionable "Next step" hint tailored to the actual failure instead of the generic Dynamic
/// LINQ-flavored hint, which is misleading for e.g. a C# compile error or a misconfigured
/// out-of-process query host.</summary>
public sealed class EfCoreMcpToolsQueryErrorFormattingTests
{
    [Fact]
    public async Task RunQuery_WithRoslynEngineAndInvalidCSharp_ReportsACompileErrorHint()
    {
        var tools = CreateTools(new QueryExecutionOptions
        {
            Engine = QueryEngine.Roslyn,
            Mode = QueryExecutionMode.InProcess,
        });
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.RunQuery("SampleAppDbContext", "Customers.ThisMethodDoesNotExist()"));

        Assert.Contains("could not be compiled", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Next step: Fix the reported compile error(s)", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunQuery_WithOutOfProcessModeAndNoHostConfigured_ReportsAServerConfigurationHint()
    {
        var tools = CreateTools(new QueryExecutionOptions
        {
            Engine = QueryEngine.Roslyn,
            Mode = QueryExecutionMode.OutOfProcess,
            OutOfProcessHostPath = null,
        });
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.RunQuery("SampleAppDbContext", "Customers.Select(c => c.Name)"));

        Assert.Contains("QueryExecution:OutOfProcessHostPath", exception.Message, StringComparison.Ordinal);
        Assert.Contains("server-side configuration problem", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("validate Dynamic LINQ syntax", exception.Message, StringComparison.Ordinal);
    }

    private static EfCoreMcpTools CreateTools(QueryExecutionOptions queryExecutionOptions)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Connections:Primary:ConnectionString"] = "Data Source=:memory:",
                ["Connections:Primary:Provider"] = "Sqlite",
                ["Connections:Primary:Environment"] = "Development",
                ["Connections:Primary:AccessPolicy:AllowContexts:0"] = "SampleApp.SampleAppDbContext",
            })
            .Build();
        var rawSqlOptions = new RawSqlExecutionOptions();
        var migrationsOptions = new MigrationsOptions();

        return new EfCoreMcpTools(
            new AssemblyLoaderService(),
            new AssemblyDiscoveryService(),
            new ConnectionRegistry(configuration),
            new SchemaCache(),
            new QueryExecutor(new QueryExecutionOptions(), NullLogger<QueryExecutor>.Instance),
            new RoslynQueryExecutor(queryExecutionOptions, new QueryCompiler(new QueryCompilationOptions())),
            new OutOfProcessRoslynQueryExecutor(queryExecutionOptions),
            queryExecutionOptions,
            rawSqlOptions,
            new SqlQueryExecutor(rawSqlOptions, NullLogger<SqlQueryExecutor>.Instance),
            migrationsOptions,
            new MigrationInspector(migrationsOptions, NullLogger<MigrationInspector>.Instance),
            new JsonToolResultFormatter(),
            new ToolDiagnosticsOptions(),
            NullLogger<EfCoreMcpTools>.Instance,
            new EntityMutationsOptions(),
            new EntityMutationExecutor(NullLogger<EntityMutationExecutor>.Instance));
    }
}
