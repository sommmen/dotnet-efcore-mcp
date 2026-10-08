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
    // SQL Server / generic.
    [InlineData("Login failed. Server=tcp:db.example.com,1433;Database=app;User ID=sa;Password=hunter2", "hunter2")]
    [InlineData("Failed: Data Source=/var/app.db;Password=s3cret", "s3cret")]
    // A quoted value may itself contain the delimiter, so stopping at the first ';' leaks the tail.
    [InlineData("Login failed for user. Password=\"abc;hunter2\";Database=app", "hunter2")]
    [InlineData("Login failed for user. Password='abc;s3cret';Database=app", "s3cret")]
    // Npgsql spells these differently and they were not matched at all.
    [InlineData("Npgsql error. Host=db.internal;Username=postgres;Password=hunter2", "hunter2")]
    [InlineData("Npgsql error. Host=db.internal;Password=s3cret;Database=app", "s3cret")]
    // Doubled quotes are the escape form inside a quoted value.
    [InlineData("Password=\"ab\"\"cd;hunter2\";Database=app", "hunter2")]
    public void Redact_RemovesCredentialBearingSegments(string message, string secret)
    {
        var redacted = SensitiveTextRedactor.Redact(message);

        Assert.DoesNotContain(secret, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("The query could not be translated or executed by the database provider.")]
    [InlineData("Unable to execute the query in the out-of-process query host.")]
    public void FormatQueryError_RedactsCredentialsFromTheSurfacedCause(string outerMessage)
    {
        // run_query/preview_query_sql surface the cause too, and a provider can embed its
        // connection details in that text, so this path must redact like the others.
        var exception = new QueryExecutionException(
            outerMessage,
            new InvalidOperationException("Login failed. Server=db.internal;User ID=sa;Password=hunter2"));

        var message = EfCoreMcpTools.FormatQueryErrorForTesting(exception);

        Assert.DoesNotContain("hunter2", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Login failed.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatQueryError_LeavesTranslationDiagnosticsIntact()
    {
        // The guarantee from issue #85 must survive redaction.
        var exception = new QueryExecutionException(
            "The query could not be translated or executed by the database provider.",
            new InvalidOperationException("The LINQ expression 'c.Name.Normalize()' could not be translated."));

        var message = EfCoreMcpTools.FormatQueryErrorForTesting(exception);

        Assert.Contains("Normalize", message, StringComparison.Ordinal);
        Assert.Contains("could not be translated", message, StringComparison.Ordinal);
        Assert.DoesNotContain("[REDACTED]", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_PreservesNonSecretKeywordsFollowingARedactedValue()
    {
        // Redaction must not swallow the rest of the message: the surrounding diagnostic text is
        // the actionable part and has to survive.
        var redacted = SensitiveTextRedactor.Redact(
            "Login failed. Password=hunter2;Database=app. Next step: check credentials.");

        Assert.DoesNotContain("hunter2", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Next step: check credentials.", redacted, StringComparison.Ordinal);
        Assert.Contains("Login failed.", redacted, StringComparison.Ordinal);
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
    public void FormatUnexpected_WithDiagnosticsEnabled_AlsoNamesTheOuterFailureCategory()
    {
        // ExposeSafeErrorDetails no longer gates whether the cause is disclosed. It still has a
        // job: adding the outer exception's type, which the flattened cause deliberately hides and
        // which matters when the wrapper itself is the interesting part (e.g. a load failure).
        var exception = new System.Reflection.TargetInvocationException(
            new InvalidOperationException("inner detail"));

        var withDetails = EfCoreMcpTools.FormatUnexpectedToolFailure("run_query", exception, "ref1", exposeSafeErrorDetails: true);
        var withoutDetails = EfCoreMcpTools.FormatUnexpectedToolFailure("run_query", exception, "ref1", exposeSafeErrorDetails: false);

        Assert.Contains("Failure category: TargetInvocationException", withDetails, StringComparison.Ordinal);
        Assert.DoesNotContain("Failure category", withoutDetails, StringComparison.Ordinal);
        // The cause reaches the caller either way - that is the issue #85 guarantee.
        Assert.Contains("inner detail", withDetails, StringComparison.Ordinal);
        Assert.Contains("inner detail", withoutDetails, StringComparison.Ordinal);
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
        var wrapper = new MigrationInspectionException("Migration inspection failed.", inner);

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
        var wrapper = new MigrationInspectionException(
            "The migration 'X' was not found. Next step: call list_migrations.", inner);

        var message = EfCoreMcpTools.FormatSubsystemError(wrapper);

        Assert.Equal("The migration 'X' was not found. Next step: call list_migrations.", message);
    }

    [Theory]
    // `==` and `=>` are not keyword assignments, so the value pattern must not start matching at
    // them - otherwise it eats the rest of an EF expression and destroys the diagnostic.
    [InlineData("The LINQ expression 'u => u.Token == Normalize(value)' could not be translated.")]
    [InlineData("Translation failed: x.Secret == y.Secret")]
    [InlineData("Predicate 'c => c.Password == input' is not supported.")]
    public void Redact_LeavesComparisonsAndLambdasInExpressionsIntact(string diagnostic)
    {
        Assert.Equal(diagnostic, SensitiveTextRedactor.Redact(diagnostic));
    }

    [Theory]
    // A truncated diagnostic can leave a quoted value unterminated. Falling back to the unquoted
    // branch then stops at the first ';' and leaks the remainder, so an unterminated quote must be
    // treated conservatively and redacted through to the end of the text.
    [InlineData("Login failed. Password=\"abc;hunter2;Database=app", "hunter2")]
    [InlineData("Login failed. Password='abc;hunter2;Database=app", "hunter2")]
    [InlineData("Server=\"db;Password=hunter2", "hunter2")]
    // Provider diagnostics are frequently multi-line, so the unterminated branch must span
    // newlines; '.' excludes them unless Singleline is set.
    [InlineData("Login failed. Password=\"abc\nhunter2;Database=app", "hunter2")]
    [InlineData("Login failed. Password='abc\r\nhunter2;Database=app", "hunter2")]
    public void Redact_RedactsThroughEndOfTextForAnUnterminatedQuotedValue(string message, string secret)
    {
        var redacted = SensitiveTextRedactor.Redact(message);

        Assert.DoesNotContain(secret, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_StillRedactsASingleEqualsAssignmentAfterAComparison()
    {
        // Guards the negative lookahead: skipping `==` must not also skip a real assignment.
        var redacted = SensitiveTextRedactor.Redact("x == y. Password=hunter2;Database=app");

        Assert.DoesNotContain("hunter2", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x == y.", redacted, StringComparison.Ordinal);
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
