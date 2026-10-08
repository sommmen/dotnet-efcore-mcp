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

/// <summary>Covers the guarantee added for issue #85: every MCP tool failure surfaces the real
/// underlying diagnostic (EF Core translation errors, provider SQL errors) rather than an opaque
/// wrapper, because that text is what lets a calling agent correct its own query. The only thing
/// withheld is credential material, which <see cref="SensitiveTextRedactor"/> removes.</summary>
public sealed class EfCoreMcpToolsErrorDetailTests
{
    [Fact]
    public void Describe_FlattensTargetInvocationAndAggregateWrappers()
    {
        // EF Core surfaces translation failures behind these wrappers, whose own messages
        // ("Exception has been thrown by the target of an invocation.") say nothing useful.
        var root = new InvalidOperationException("The LINQ expression 'Normalize()' could not be translated.");
        var wrapped = new System.Reflection.TargetInvocationException(new AggregateException(root));

        var detail = QueryExceptionDetail.Describe(wrapped);

        Assert.Equal("InvalidOperationException: The LINQ expression 'Normalize()' could not be translated.", detail);
    }

    [Fact]
    public void Describe_ReturnsNullWhenThereIsNoUsableDetail()
    {
        Assert.Null(QueryExceptionDetail.Describe(null));
    }

    [Theory]
    [InlineData("Login failed. Server=tcp:db.example.com,1433;Database=app;User ID=sa;Password=hunter2")]
    [InlineData("Failed: Data Source=/var/app.db;Password=s3cret")]
    public void Redact_RemovesCredentialBearingSegments(string message)
    {
        var redacted = SensitiveTextRedactor.Redact(message);

        Assert.DoesNotContain("hunter2", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("s3cret", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunSqlQuery_WithInvalidSql_SurfacesTheProviderErrorText()
    {
        // Raw SQL failures carry the provider's own parse/missing-table diagnostic, which is the
        // actionable part for an agent writing SQL.
        using var db = new SqliteTestDatabase();
        var tools = CreateToolsWithRawSql(db);
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.RunSqlQuery("SampleAppDbContext", "SELECT * FROM ThisTableDoesNotExist"));

        Assert.Contains("ThisTableDoesNotExist", exception.Message, StringComparison.Ordinal);
    }

    private static EfCoreMcpTools CreateToolsWithRawSql(SqliteTestDatabase db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Connections:Primary:ConnectionString"] = db.ConnectionString,
                ["Connections:Primary:Provider"] = "Sqlite",
                ["Connections:Primary:Environment"] = "Development",
                ["Connections:Primary:AccessMode"] = "ReadWrite",
                ["Connections:Primary:AccessPolicy:AllowContexts:0"] = "SampleApp.SampleAppDbContext",
            })
            .Build();
        var rawSqlOptions = new RawSqlExecutionOptions { Enabled = true };
        var queryExecutionOptions = new QueryExecutionOptions { Mode = QueryExecutionMode.InProcess };
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
            NullLogger<EfCoreMcpTools>.Instance,
            new EntityMutationsOptions(),
            new EntityMutationExecutor(NullLogger<EntityMutationExecutor>.Instance));
    }

    [Fact]
    public void FormatUnexpected_IncludesTheFlattenedDiagnostic_EvenOutsideDevelopment()
    {
        // The catch-all path previously replaced every message with "failed unexpectedly. Error
        // reference: <guid>" unless ToolDiagnostics:ExposeSafeErrorDetails was on AND the host was
        // Development - so in a normal deployment an EF Core or provider error never reached the
        // agent at all. The reference is still emitted for log correlation, alongside the detail.
        var exception = new System.Reflection.TargetInvocationException(
            new InvalidOperationException("The LINQ expression 'Foo()' could not be translated."));

        var message = EfCoreMcpTools.FormatUnexpectedToolFailure("run_query", exception, "abc123");

        Assert.Contains("abc123", message, StringComparison.Ordinal);
        Assert.Contains("could not be translated", message, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatUnexpected_RedactsCredentialsFromTheDiagnostic()
    {
        var exception = new InvalidOperationException(
            "Login failed for Server=tcp:db.example.com;User ID=sa;Password=hunter2");

        var message = EfCoreMcpTools.FormatUnexpectedToolFailure("test_connection", exception, "ref1");

        Assert.DoesNotContain("hunter2", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Login failed", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSubsystemError_AppendsTheFlattenedCauseWhenTheWrapperOmitsIt()
    {
        // Migration and assembly-loading failures reach the client through their own exception
        // types, whose outer message sometimes omits the provider/runtime detail held in the inner
        // exception. Those paths must surface the cause too, for the same reason as run_query.
        var inner = new InvalidOperationException("The migration 'X' was not found.");
        var wrapper = new Server.Migrations.MigrationInspectionException("Migration inspection failed.", inner);

        var message = EfCoreMcpTools.FormatSubsystemError(wrapper);

        Assert.Contains("Migration inspection failed.", message, StringComparison.Ordinal);
        Assert.Contains("The migration 'X' was not found.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSubsystemError_DoesNotRepeatACauseAlreadyInlinedInTheMessage()
    {
        // MigrationInspector already inlines the provider text for its common case; the cause must
        // not then appear twice.
        var inner = new InvalidOperationException("The migration 'X' was not found.");
        var wrapper = new Server.Migrations.MigrationInspectionException(
            "The migration 'X' was not found. Next step: call list_migrations.", inner);

        var message = EfCoreMcpTools.FormatSubsystemError(wrapper);

        Assert.Equal("The migration 'X' was not found. Next step: call list_migrations.", message);
    }

    [Fact]
    public void Redact_LeavesOrdinaryEfDiagnosticsIntact()
    {
        // The whole point of issue #85: this text must reach the caller verbatim.
        const string message =
            "The LINQ expression 'DbSet<Customer>().Where(c => c.Name.Normalize() == \"x\")' could not be translated. " +
            "Additional information: Translation of method 'string.Normalize' failed.";

        Assert.Equal(message, SensitiveTextRedactor.Redact(message));
    }
}
