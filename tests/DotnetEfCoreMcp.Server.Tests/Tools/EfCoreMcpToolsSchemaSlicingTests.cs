using System.Text.Json;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.Migrations;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Schema;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using DotnetEfCoreMcp.Server.Tools;
using DotnetEfCoreMcp.Server.Mutations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace DotnetEfCoreMcp.Server.Tests.Tools;

/// <summary>Binding/forwarding tests for the `get_entity_schema` and `search_schema` MCP tools (P0
/// #6). Both tools are cache-only: every test here first calls `get_schema` to populate
/// <see cref="SchemaCache"/>, matching how a real MCP client is expected to use them, then asserts
/// the two new tools only ever read from that cache.</summary>
public sealed class EfCoreMcpToolsSchemaSlicingTests
{
    [Fact]
    public void GetEntitySchema_WithExactName_ReturnsCompleteEntityDefinition()
    {
        var tools = CreateTools();
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);
        tools.GetSchema("SampleAppDbContext");

        using var document = JsonDocument.Parse(tools.GetEntitySchema("Order", "SampleAppDbContext"));
        var root = document.RootElement;

        Assert.Equal("SampleAppDbContext", root.GetProperty("contextName").GetString());
        var entity = root.GetProperty("entity");
        // EntityTypeSchema is serialized as-is (same as get_schema's `entities`), so its record
        // properties keep their PascalCase names rather than the camelCase used for the tool's own
        // top-level response properties.
        Assert.Equal("Order", entity.GetProperty("Name").GetString());
        Assert.True(entity.GetProperty("Properties").GetArrayLength() > 0);
        Assert.True(entity.GetProperty("ForeignKeys").GetArrayLength() > 0);
    }

    [Fact]
    public void GetEntitySchema_WithUnknownEntity_ThrowsAndListsKnownEntities()
    {
        var tools = CreateTools();
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);
        tools.GetSchema("SampleAppDbContext");

        var exception = Assert.Throws<McpException>(() => tools.GetEntitySchema("NoSuchEntity", "SampleAppDbContext"));

        Assert.Contains("NoSuchEntity", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Customer", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Order", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetEntitySchema_WithEmptyEntityName_ThrowsValidationError()
    {
        var tools = CreateTools();
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);
        tools.GetSchema("SampleAppDbContext");

        var exception = Assert.Throws<McpException>(() => tools.GetEntitySchema("   ", "SampleAppDbContext"));

        Assert.Contains("entityName", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetEntitySchema_WhenSchemaWasNeverBuilt_ThrowsWithoutConstructingAContext()
    {
        var tools = CreateTools();
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        // get_schema was never called for this context, so nothing is cached yet. If this call
        // attempted to build the schema itself, it would need a working connection/context and
        // would either succeed unexpectedly or fail with a database error instead of this
        // cache-miss message.
        var exception = Assert.Throws<McpException>(() => tools.GetEntitySchema("Order", "SampleAppDbContext"));

        Assert.Contains("get_schema", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchSchema_ReturnsCompactMatchesNotFullDefinitions()
    {
        var tools = CreateTools();
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);
        tools.GetSchema("SampleAppDbContext");

        using var document = JsonDocument.Parse(tools.SearchSchema("SampleAppDbContext", "Customer"));
        var root = document.RootElement;

        var matches = root.GetProperty("matches");
        Assert.True(matches.GetArrayLength() > 0);
        var match = matches[0];
        // SchemaSearchMatch is serialized as-is, so its record properties keep their PascalCase
        // names. The point of this assertion is what's absent: no "ForeignKeys"/"Properties" keys
        // (the full entity shape from get_entity_schema), only the compact match shape.
        Assert.False(match.TryGetProperty("ForeignKeys", out _));
        Assert.False(match.TryGetProperty("Properties", out _));
        Assert.True(match.TryGetProperty("EntityName", out _));
    }

    [Fact]
    public void SearchSchema_IsCaseInsensitive()
    {
        var tools = CreateTools();
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);
        tools.GetSchema("SampleAppDbContext");

        using var document = JsonDocument.Parse(tools.SearchSchema("SampleAppDbContext", "customer"));

        var entityNames = document.RootElement.GetProperty("matches")
            .EnumerateArray()
            .Select(m => m.GetProperty("EntityName").GetString())
            .ToArray();
        Assert.Contains("Customer", entityNames);
    }

    [Fact]
    public void SearchSchema_WithEmptyQuery_ThrowsValidationError()
    {
        var tools = CreateTools();
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);
        tools.GetSchema("SampleAppDbContext");

        var exception = Assert.Throws<McpException>(() => tools.SearchSchema("SampleAppDbContext", ""));

        Assert.Contains("query", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(26)]
    [InlineData(-1)]
    public void SearchSchema_WithInvalidMaxResults_ThrowsValidationError(int maxResults)
    {
        var tools = CreateTools();
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);
        tools.GetSchema("SampleAppDbContext");

        var exception = Assert.Throws<McpException>(() => tools.SearchSchema("SampleAppDbContext", "Customer", maxResults));

        Assert.Contains("maxResults", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchSchema_DefaultsMaxResultsToTen()
    {
        var tools = CreateTools();
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);
        tools.GetSchema("SampleAppDbContext");

        using var document = JsonDocument.Parse(tools.SearchSchema("SampleAppDbContext", "e"));

        Assert.Equal(10, document.RootElement.GetProperty("maxResults").GetInt32());
    }

    [Fact]
    public void SearchSchema_AcceptsTheAbsoluteMaximumOfTwentyFive()
    {
        var tools = CreateTools();
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);
        tools.GetSchema("SampleAppDbContext");

        using var document = JsonDocument.Parse(tools.SearchSchema("SampleAppDbContext", "e", 25));

        Assert.Equal(25, document.RootElement.GetProperty("maxResults").GetInt32());
    }

    [Fact]
    public void SearchSchema_ReportsTruncatedWhenMoreMatchesExistThanTheCap()
    {
        var tools = CreateTools();
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);
        tools.GetSchema("SampleAppDbContext");

        // "e" matches many property/entity names across the small SampleApp fixture (Name, Age,
        // CreatedAtUtc, Amount, Customer, Orders, ...), comfortably exceeding a maxResults of 1.
        using var document = JsonDocument.Parse(tools.SearchSchema("SampleAppDbContext", "e", 1));
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("matches").GetArrayLength());
        Assert.True(root.GetProperty("totalMatchCount").GetInt32() > 1);
        Assert.True(root.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void SearchSchema_WithNoMatches_ReturnsEmptyResultsAndNotTruncated()
    {
        var tools = CreateTools();
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);
        tools.GetSchema("SampleAppDbContext");

        using var document = JsonDocument.Parse(tools.SearchSchema("SampleAppDbContext", "zzz-no-match-zzz"));
        var root = document.RootElement;

        Assert.Equal(0, root.GetProperty("matches").GetArrayLength());
        Assert.Equal(0, root.GetProperty("totalMatchCount").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void SearchSchema_DeclaresContextNameAsOptional()
    {
        var parameter = typeof(EfCoreMcpTools)
            .GetMethod(nameof(EfCoreMcpTools.SearchSchema), new[] { typeof(string), typeof(string), typeof(int?) })?
            .GetParameters()[0];

        Assert.NotNull(parameter);
        Assert.True(parameter!.HasDefaultValue, "The MCP tool metadata should mark contextName as optional so single-context assemblies can omit it.");
    }

    [Fact]
    public void SearchSchema_WhenSchemaWasNeverBuilt_ThrowsWithoutConstructingAContext()
    {
        var tools = CreateTools();
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = Assert.Throws<McpException>(() => tools.SearchSchema("SampleAppDbContext", "Customer"));

        Assert.Contains("get_schema", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetEntitySchema_WhenContextNameIsOmittedWithMultipleContexts_ListsShortNames()
    {
        var tools = CreateTools();
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);

        var exception = Assert.Throws<McpException>(() => tools.GetEntitySchema("Order"));

        Assert.Contains("list_contexts", exception.Message, StringComparison.Ordinal);
    }

    private static EfCoreMcpTools CreateTools(
        IToolResultFormatter? resultFormatter = null,
        ToolDiagnosticsOptions? toolDiagnosticsOptions = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Connections:SchemaTests:ConnectionString"] = "Data Source=:memory:",
                ["Connections:SchemaTests:Provider"] = "Sqlite",
                ["Connections:SchemaTests:AccessMode"] = "ReadOnly",
                ["Connections:SchemaTests:Environment"] = "Development",
                ["Connections:SchemaTests:AccessPolicy:AllowContexts:0"] = "SampleApp.SampleAppDbContext",
            })
            .Build();
        var rawSqlOptions = new RawSqlExecutionOptions();

        return new EfCoreMcpTools(
            new AssemblyLoaderService(),
            new AssemblyDiscoveryService(),
            new ConnectionRegistry(configuration),
            new SchemaCache(),
            new QueryExecutor(new QueryExecutionOptions(), NullLogger<QueryExecutor>.Instance),
            new RoslynQueryExecutor(new QueryExecutionOptions(), new QueryCompiler(new QueryCompilationOptions())),
            new OutOfProcessRoslynQueryExecutor(new QueryExecutionOptions()),
            new QueryExecutionOptions(),
            rawSqlOptions,
            new SqlQueryExecutor(rawSqlOptions, NullLogger<SqlQueryExecutor>.Instance),
            new MigrationsOptions(),
            new MigrationInspector(new MigrationsOptions(), NullLogger<MigrationInspector>.Instance),
            resultFormatter ?? new JsonToolResultFormatter(),
            toolDiagnosticsOptions ?? new ToolDiagnosticsOptions(),
            NullLogger<EfCoreMcpTools>.Instance,
            new EntityMutationsOptions(),
            new EntityMutationExecutor(NullLogger<EntityMutationExecutor>.Instance));
    }
}
