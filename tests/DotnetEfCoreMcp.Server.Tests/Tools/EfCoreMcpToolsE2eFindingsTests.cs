using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.Hosting;
using DotnetEfCoreMcp.Server.Migrations;
using DotnetEfCoreMcp.Server.Mutations;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Schema;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using DotnetEfCoreMcp.Server.Tools;
using DotnetEfCoreMcp.Server.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace DotnetEfCoreMcp.Server.Tests.Tools;

/// <summary>Regression coverage for the findings of the end-to-end run against two real
/// applications: required-parameter validation, query and cursor error text, log noise, the advertised
/// server version, query-execution-mode selection and the default-target alias.</summary>
public sealed class EfCoreMcpToolsE2EFindingsTests
{
    [Theory]
    [InlineData("load_assembly", "assemblyPath")]
    [InlineData("swap_connection", "connectionName")]
    [InlineData("list_assembly_candidates", "workspacePath")]
    public void MissingRequiredParameter_IsAValidationErrorNotAnUnexpectedFailure(string tool, string parameter)
    {
        var tools = CreateTools();

        var exception = Assert.Throws<McpException>(() => _ = tool switch
        {
            "load_assembly" => tools.LoadAssembly(null!),
            "swap_connection" => tools.SwapConnection(null!),
            _ => tools.ListAssemblyCandidates(null!),
        });

        Assert.Equal($"{parameter} is required.", exception.Message);
    }

    [Fact]
    public async Task RunQuery_WithATamperedCursor_FailsFastWithAnInvalidOrExpiredMessage()
    {
        using var db = new SqliteTestDatabase();
        var tools = CreateTools(db);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(() => tools.RunQuery(
            "SampleAppDbContext", "Customers.OrderBy(c => c.Id)",
            pagination: new QueryPagination { Mode = "cursor", Cursor = "e30.AAAA" },
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("invalid or expired", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Cryptographic", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("get_schema", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunQuery_CursorWithAProjection_TellsTheCallerToPaginateEntities()
    {
        using var db = new SqliteTestDatabase();
        var tools = CreateTools(db);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(() => tools.RunQuery(
            "SampleAppDbContext", "Customers.OrderBy(c => c.Id).Select(c => c.Name)",
            pagination: new QueryPagination { Mode = "cursor" },
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("drop Select()", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("get_schema", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunQuery_CursorReusedWithADifferentQuery_ReportsTheMismatch()
    {
        using var db = new SqliteTestDatabase();
        var tools = CreateTools(db);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);
        EnsureSchemaWithCustomers(db, count: 3);
        var first = await tools.RunQuery(
            "SampleAppDbContext", "Customers.OrderBy(c => c.Id).Take(1)",
            pagination: new QueryPagination { Mode = "cursor" },
            cancellationToken: TestContext.Current.CancellationToken);
        var cursor = System.Text.RegularExpressions.Regex.Match(first, "\"NextCursor\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(cursor);

        var exception = await Assert.ThrowsAsync<McpException>(() => tools.RunQuery(
            "SampleAppDbContext", "Customers.OrderBy(c => c.Age).Take(1)",
            pagination: new QueryPagination { Mode = "cursor", Cursor = cursor },
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("issued for a different query", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("get_schema", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatQueryError_RowLimitWithoutOrderBy_NamesTheRealCauseAndSuggestsOrderBy()
    {
        var exception = new QueryExecutionException(
            "The query could not be translated or executed by the database provider.",
            new InvalidOperationException("Error generated for warning 'Microsoft.EntityFrameworkCore.Query.RowLimitingOperationWithoutOrderByWarning'."));

        var message = EfCoreMcpTools.FormatQueryErrorForTesting(exception);

        Assert.DoesNotContain("could not be translated or executed", message, StringComparison.Ordinal);
        Assert.Contains("RowLimitingOperationWithoutOrderBy", message, StringComparison.Ordinal);
        Assert.Contains("add an explicit OrderBy", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatQueryError_DistinctAfterOrderBy_SuggestsMovingTheOrdering()
    {
        var exception = new QueryExecutionException("Query failed.",
            new InvalidOperationException("DistinctAfterOrderByWithoutRowLimitingOperatorWarning"));

        var message = EfCoreMcpTools.FormatQueryErrorForTesting(exception);

        Assert.Contains("OrderBy after Distinct", message, StringComparison.Ordinal);
        Assert.DoesNotContain("get_schema", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Include path segment 'Bogus' is not a navigation on 'Customer'.")]
    [InlineData("`query` must be a non-empty LINQ expression.")]
    [InlineData("Cursor pagination cannot be combined with LINQ Skip().")]
    public void FormatQueryError_InputValidationFailures_DoNotSuggestGetSchema(string message)
    {
        var formatted = EfCoreMcpTools.FormatQueryErrorForTesting(new QueryExecutionException(message));

        Assert.StartsWith(message, formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("verify entity and property names with get_schema", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void SwapConnection_RefusedProductionSwap_IsLoggedAtWarningWithoutAStackTrace()
    {
        var logger = new CapturingLogger();
        var tools = CreateTools(logger: logger, environment: "Production");

        Assert.Throws<McpException>(() => tools.SwapConnection("Primary"));

        var entry = Assert.Single(logger.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public void LoadAssembly_ScanWarnings_AreReturnedInTheResultButNotRepeatedAsWarningLogs()
    {
        var logger = new CapturingLogger();
        var tools = CreateTools(logger: logger);

        var json = tools.LoadAssembly(FixturePaths.NoContextAppDllPath);

        Assert.Contains("referenced assembly", json, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public void ServerInfo_AdvertisesThePackageVersionNotTheAssemblyVersion()
    {
        var serverInfo = ServerComposition.CreateServerInfo();

        Assert.Equal(PackageVersionProvider.GetPackageVersion(), serverInfo.Version);
        Assert.NotEqual("0.1.0.0", serverInfo.Version);
    }

    [Theory]
    [InlineData(QueryExecutionMode.Auto, true, QueryExecutionMode.Pooled)]
    [InlineData(QueryExecutionMode.Auto, false, QueryExecutionMode.OutOfProcess)]
    [InlineData(QueryExecutionMode.InProcess, true, QueryExecutionMode.InProcess)]
    [InlineData(QueryExecutionMode.OutOfProcess, true, QueryExecutionMode.OutOfProcess)]
    [InlineData(QueryExecutionMode.Pooled, false, QueryExecutionMode.Pooled)]
    public void ResolveAutoMode_PrefersThePoolOnlyForLongLivedHosts(QueryExecutionMode configured, bool longLivedHost, QueryExecutionMode expected)
    {
        var options = new QueryExecutionOptions { Mode = configured };

        Assert.Equal(expected, options.ResolveAutoMode(longLivedHost).Mode);
    }

    [Theory]
    [InlineData(true, QueryExecutionMode.Pooled)]
    [InlineData(false, QueryExecutionMode.OutOfProcess)]
    public void AddEfCoreServices_ResolvesTheDefaultAutoModeFromTheHostKind(bool includeHostedServices, QueryExecutionMode expected)
    {
        var builder = Host.CreateApplicationBuilder();

        ServerComposition.AddEfCoreServices(builder, includeHostedServices);
        using var provider = builder.Services.BuildServiceProvider();

        Assert.Equal(expected, provider.GetRequiredService<QueryExecutionOptions>().Mode);
    }

    [Fact]
    public void SelectTarget_DefaultAlias_SelectsTheTargetLoadedWithoutAName()
    {
        var tools = CreateTools();
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);
        tools.LoadAssembly(FixturePaths.NoContextAppDllPath, "other");
        tools.SelectTarget("other");

        var selected = tools.SelectTarget("default");
        var listed = tools.ListLoadedAssemblies();

        Assert.Contains("\"defaultTargetName\": \"default\"", selected, StringComparison.Ordinal);
        Assert.Contains("\"targetName\": \"default\"", listed, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectTarget_OntoAnAssemblyWithoutDbContexts_WarnsThatContextsMayBeReferenced()
    {
        var tools = CreateTools();
        tools.LoadAssembly(FixturePaths.NoContextAppDllPath, "empty");

        var json = tools.SelectTarget("empty");

        Assert.Contains("No DbContext-derived types", json, StringComparison.Ordinal);
        Assert.Contains("referenced assembly", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AssemblyLoader_DefaultAlias_ResolvesToTheImplicitTargetAndCannotBeRegisteredAsAName()
    {
        var service = new AssemblyLoaderService();
        var implicitTarget = service.Load(FixturePaths.SampleAppDllPath);

        Assert.Same(implicitTarget, service.Get("default"));
        Assert.Same(implicitTarget, service.Get("Default"));

        var exception = Assert.Throws<AssemblyLoadFailedException>(
            () => service.Load(FixturePaths.NoContextAppDllPath, "default"));

        Assert.Contains("reserved", exception.Message, StringComparison.Ordinal);
        Assert.Same(implicitTarget, service.Get());
        Assert.Single(service.ListTargets());
    }
    private static void EnsureSchemaWithCustomers(SqliteTestDatabase db, int count)
    {
        var handle = new AssemblyLoaderService().Load(FixturePaths.SampleAppDllPath);
        var contextType = Server.DbContextDiscovery.DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors
            .Single(d => d.Name == "SampleAppDbContext").ClrType;
        using var context = Server.DbContextDiscovery.DbContextActivator.CreateInstance(contextType, db.ToRegistryEntry(), DatabaseProvider.Sqlite);
        context.Database.EnsureCreated();
        var customerType = EntitySeeding.GetEntityClrType(context, "Customer");
        for (var i = 1; i <= count; i++)
        {
            context.Add(EntitySeeding.CreateEntity(customerType, new Dictionary<string, object?> { ["Name"] = $"C{i}", ["Age"] = 20 + i }));
        }

        context.SaveChanges();
    }

    private static EfCoreMcpTools CreateTools(SqliteTestDatabase? db = null, ILogger<EfCoreMcpTools>? logger = null, string environment = "Development")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Connections:Primary:ConnectionString"] = db?.ConnectionString ?? "Data Source=:memory:",
                ["Connections:Primary:Provider"] = "Sqlite",
                ["Connections:Primary:Environment"] = environment,
                ["Connections:Primary:AccessPolicy:AllowContexts:0"] = "SampleApp.SampleAppDbContext",
            })
            .Build();
        var queryExecutionOptions = new QueryExecutionOptions { Mode = QueryExecutionMode.InProcess };
        var rawSqlOptions = new RawSqlExecutionOptions();
        var migrationsOptions = new MigrationsOptions();

        return new EfCoreMcpTools(
            new AssemblyLoaderService(),
            new AssemblyDiscoveryService(),
            new ConnectionRegistry(configuration),
            new SchemaCache(),
            new RoslynQueryExecutor(queryExecutionOptions, new QueryCompiler(new QueryCompilationOptions())),
            new OutOfProcessRoslynQueryExecutor(queryExecutionOptions),
            queryExecutionOptions,
            rawSqlOptions,
            new SqlQueryExecutor(rawSqlOptions, NullLogger<SqlQueryExecutor>.Instance),
            migrationsOptions,
            new MigrationInspector(migrationsOptions, NullLogger<MigrationInspector>.Instance),
            new JsonToolResultFormatter(),
            new ToolDiagnosticsOptions(),
            logger ?? NullLogger<EfCoreMcpTools>.Instance,
            new EntityMutationsOptions(),
            new EntityMutationExecutor(NullLogger<EntityMutationExecutor>.Instance));
    }

    private sealed class CapturingLogger : ILogger<EfCoreMcpTools>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception));
    }
}
