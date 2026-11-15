using System.Text.Json;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.Migrations;
using DotnetEfCoreMcp.Server.Mutations;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Schema;
using DotnetEfCoreMcp.Server.Telemetry;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using DotnetEfCoreMcp.Server.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetEfCoreMcp.Server.Tests.Cli;

[Collection(ApplicationFactoryEnvironmentCollection.Name)]
public sealed class CliOperationTests : IDisposable
{
    private readonly CliTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task ListContexts_PrintsTheDiscoveredContexts()
    {
        var result = await _host.RunAsync("list_contexts", "--assembly", FixturePaths.SampleAppDllPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("SampleAppDbContext", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutputJson_RendersTheToolResultAsJson()
    {
        var result = await _host.RunAsync("list_contexts", "--assembly", FixturePaths.SampleAppDllPath, "--output", "json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Contains(
            document.RootElement.GetProperty("contexts").EnumerateArray(),
            context => context.GetProperty("name").GetString() == "SampleAppDbContext");
    }

    [Fact]
    public async Task MissingRequiredParameter_IsAUsageError()
    {
        var result = await _host.RunAsync("swap_connection");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--connection-name", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectionFlag_ActivatesTheConnectionForOperationsWithoutAConnectionParameter()
    {
        var result = await _host.RunAsync(
            "list_contexts", "--assembly", FixturePaths.SampleAppDllPath, "--connection", CliTestHost.ConnectionName, "--output", "json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Contains($"connection '{CliTestHost.ConnectionName}'", document.RootElement.GetProperty("warnings")[0].GetString(), StringComparison.Ordinal);
        Assert.Contains(
            document.RootElement.GetProperty("contexts").EnumerateArray(),
            context => context.GetProperty("name").GetString() == "SampleAppDbContext");

        var session = await _host.RunAsync("session", "show");
        Assert.DoesNotContain("activeConnection", session.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KebabCaseOperationNames_AreAccepted()
    {
        var snake = await _host.RunAsync("list_contexts", "--assembly", FixturePaths.SampleAppDllPath);
        var kebab = await _host.RunAsync("list-contexts", "--assembly", FixturePaths.SampleAppDllPath);

        Assert.Equal(0, kebab.ExitCode);
        Assert.Equal(snake.Output, kebab.Output);
    }

    [Fact]
    public async Task GetSchema_PrintsTheEntityModel()
    {
        var result = await _host.RunAsync("get_schema", "--assembly", FixturePaths.SampleAppDllPath, "--context-name", "SampleAppDbContext", "--connection", CliTestHost.ConnectionName);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Customer", result.Output, StringComparison.Ordinal);
        Assert.Contains("Order", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunQuery_ReturnsTheSeededRows()
    {
        var result = await _host.RunAsync(
            "run_query", "--assembly", FixturePaths.SampleAppDllPath, "--connection", CliTestHost.ConnectionName,
            "--context-name", "SampleAppDbContext", "--query", "Customers.OrderBy(c => c.Id).Select(c => c.Name)");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Customer 1", result.Output, StringComparison.Ordinal);
        Assert.Contains("Customer 3", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewQuerySql_ReturnsTheTranslatedSql()
    {
        var result = await _host.RunAsync(
            "preview_query_sql", "--assembly", FixturePaths.SampleAppDllPath, "--connection", CliTestHost.ConnectionName,
            "--context-name", "SampleAppDbContext", "--query", "Customers.Where(c => c.Age > 18).Select(c => c.Name)");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("SELECT", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Customers", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunQuery_CursorPaginationContinuesAcrossTwoSeparateCliRuns()
    {
        var common = new[]
        {
            "run_query", "--assembly", FixturePaths.SampleAppDllPath, "--connection", CliTestHost.ConnectionName,
            "--context-name", "SampleAppDbContext", "--query", "Customers.OrderBy(c => c.Id).Take(2)", "--config", "ToolOutput:Format=json",
        };

        var first = await _host.RunAsync([.. common, "--pagination", """{"mode":"cursor"}"""]);
        Assert.Equal(0, first.ExitCode);
        using var firstDocument = JsonDocument.Parse(first.Output);
        Assert.True(firstDocument.RootElement.GetProperty("HasMoreRows").GetBoolean());
        var cursor = firstDocument.RootElement.GetProperty("NextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        var second = await _host.RunAsync([.. common, "--pagination", JsonSerializer.Serialize(new { mode = "cursor", cursor })]);

        Assert.Equal(0, second.ExitCode);
        using var secondDocument = JsonDocument.Parse(second.Output);
        Assert.False(secondDocument.RootElement.GetProperty("HasMoreRows").GetBoolean());
        Assert.Contains("Customer 3", second.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Customer 1", second.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_LongerThan1000Characters_IsByteIdenticalToTheToolReturnString()
    {
        var tools = CreateTools();
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);
        var expected = tools.GetSchema("SampleAppDbContext", CliTestHost.ConnectionName);
        Assert.True(expected.Length > 1000, "The fixture schema must exceed the adapter's truncation limit for this test to mean anything.");

        var result = await _host.RunAsync("get_schema", "--assembly", FixturePaths.SampleAppDllPath, "--context-name", "SampleAppDbContext", "--connection", CliTestHost.ConnectionName);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.Output);
    }

    [Fact]
    public async Task RunQuery_OutputIsByteIdenticalToTheToolReturnString()
    {
        const string query = "Customers.OrderBy(c => c.Id).Select(c => c.Name)";
        var tools = CreateTools();
        tools.LoadAssembly(FixturePaths.SampleAppDllPath);
        var expected = await tools.RunQuery("SampleAppDbContext", query, CliTestHost.ConnectionName, cancellationToken: TestContext.Current.CancellationToken);

        var result = await _host.RunAsync("run_query", "--assembly", FixturePaths.SampleAppDllPath, "--context-name", "SampleAppDbContext", "--query", query, "--connection", CliTestHost.ConnectionName);

        Assert.Equal(expected, result.Output);
    }

    [Fact]
    public async Task ConfigOverride_SwitchesTheOutputFormat()
    {
        var result = await _host.RunAsync("list_contexts", "--assembly", FixturePaths.SampleAppDllPath, "--config", "ToolOutput:Format=json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.True(document.RootElement.TryGetProperty("contexts", out _));
    }

    [Fact]
    public async Task Stdout_CarriesOnlyTheResult()
    {
        var result = await _host.RunAsync("list_contexts", "--assembly", FixturePaths.SampleAppDllPath, "--config", "ToolOutput:Format=json");

        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.DoesNotContain("info:", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("warn:", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_ListsTheToolsAndTheSkillAndSessionCommands()
    {
        var result = await _host.RunAsync("--help");

        Assert.Equal(0, result.ExitCode);
        foreach (var name in new[] { "list-contexts", "get-schema", "run-query", "preview-query-sql", "load-assembly", "select-target", "swap-connection", "skill generate", "session show" })
        {
            Assert.Contains(name, result.Output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task OperationHelp_DescribesTheParameters()
    {
        var result = await _host.RunAsync("run_query", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--context-name", result.Output, StringComparison.Ordinal);
        Assert.Contains("--query", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode_IsOneForAnOperationError()
    {
        var result = await _host.RunAsync("run_sql_query", "--assembly", FixturePaths.SampleAppDllPath, "--context-name", "SampleAppDbContext", "--sql", "SELECT 1", "--connection", CliTestHost.ConnectionName);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("RawSqlExecution:Enabled", result.Error, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public async Task ExitCode_IsOneWhenAnExplicitAssemblyCannotBeLoaded()
    {
        var result = await _host.RunAsync("list_contexts", "--assembly", Path.Combine(AppContext.BaseDirectory, "missing", "Nope.dll"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--assembly", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("load_assembly", result.Error, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public async Task ExitCode_IsTwoForAnUnknownOperation()
    {
        var result = await _host.RunAsync("not_an_operation");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Unknown operation", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode_IsTwoForAnUnknownFlag()
    {
        var result = await _host.RunAsync("list_contexts", "--assembly", FixturePaths.SampleAppDllPath, "--bogus-flag", "1");

        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task ExitCode_IsTwoForAMalformedConfigOverride()
    {
        var result = await _host.RunAsync("list_contexts", "--config", "no-equals-sign");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--config", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitCode_Is130WhenTheRunIsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await _host.RunAsync(cancellation.Token, "list_contexts", "--assembly", FixturePaths.SampleAppDllPath);

        Assert.Equal(130, result.ExitCode);
    }

    [Fact]
    public async Task MissingTarget_UsesCliWordingInsteadOfMcpToolNames()
    {
        var emptyWorkspace = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "TestData", "empty-" + Guid.NewGuid().ToString("N")));
        try
        {
            var result = await _host.RunAsync("list_contexts", "--workspace", emptyWorkspace.FullName);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("--assembly", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            emptyWorkspace.Delete();
        }
    }

    [Fact]
    public async Task ListAssemblyCandidates_UsesTheWorkspaceFlagAsItsWorkspacePath()
    {
        using var workspace = BuildWorkspace(("Data", FixturePaths.SampleAppDllPath), ("Plain", FixturePaths.NoContextAppDllPath));

        var result = await _host.RunAsync("list-assembly-candidates", "--workspace", workspace.Path, "--config", "ToolOutput:Format=json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal(Path.GetFullPath(workspace.Path), document.RootElement.GetProperty("workspacePath").GetString());
        var first = document.RootElement.GetProperty("candidates")[0];
        Assert.EndsWith("SampleApp.dll", first.GetProperty("assemblyPath").GetString(), StringComparison.Ordinal);
        Assert.True(first.GetProperty("likelyContainsDbContext").GetBoolean());
    }

    [Fact]
    public async Task ListAssemblyCandidates_WithoutAnyWorkspaceInput_DefaultsToTheCurrentDirectory()
    {
        var result = await _host.RunAsync("list-assembly-candidates", "--config", "ToolOutput:Format=json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal(Directory.GetCurrentDirectory(), document.RootElement.GetProperty("workspacePath").GetString());
    }

    [Fact]
    public async Task WorkspaceDiscovery_LoadsTheAssemblyThatContainsADbContext()
    {
        using var workspace = BuildWorkspace(("Plain", FixturePaths.NoContextAppDllPath), ("Data", FixturePaths.SampleAppDllPath));

        var result = await _host.RunAsync("list_contexts", "--workspace", workspace.Path);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("SampleAppDbContext", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceDiscovery_WhenNoAssemblyContainsADbContext_FailsWithAHintInsteadOfLoadingOne()
    {
        using var workspace = BuildWorkspace(("Plain", FixturePaths.NoContextAppDllPath));

        var result = await _host.RunAsync("list_contexts", "--workspace", workspace.Path);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--assembly", result.Error, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public void MissingConnection_UsesCliWordingInsteadOfMcpToolNames()
    {
        var tools = CreateTools(connections: false, hints: ToolSurfaceHints.Cli);

        var exception = Assert.Throws<ModelContextProtocol.McpException>(() => tools.GetSchema("SampleAppDbContext"));

        Assert.Contains("--connection", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Call swap_connection", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingConnection_OverMcp_KeepsTheOriginalWording()
    {
        var tools = CreateTools(connections: false, hints: null);

        var exception = Assert.Throws<ModelContextProtocol.McpException>(() => tools.GetSchema("SampleAppDbContext"));

        Assert.Equal(ToolSurfaceHints.Mcp.NoActiveConnection, exception.Message);
    }

    [Fact]
    public void McpWording_IsByteIdenticalToTheOriginalMessages()
    {
        Assert.Equal(
            "No target assembly is loaded yet. Call load_assembly with the path to a compiled target project's DLL first.",
            ToolSurfaceHints.Mcp.NoTargetLoaded);
        Assert.Equal(
            "No connection is active yet. Call swap_connection, or pass an explicit connectionName. (Connections available via list_connections; call reload_connections if one you configured is missing from it.)",
            ToolSurfaceHints.Mcp.NoActiveConnection);
    }

    private static TempWorkspace BuildWorkspace(params (string Project, string SourceDll)[] projects)
    {
        var workspace = new TempWorkspace();
        foreach (var (project, sourceDll) in projects)
        {
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(workspace.Path, project)).FullName, project + ".csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>{Path.GetFileNameWithoutExtension(sourceDll)}</AssemblyName></PropertyGroup></Project>");
            var output = Directory.CreateDirectory(Path.Combine(workspace.Path, project, "bin", "Debug", "net10.0")).FullName;
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(sourceDll)!))
            {
                File.Copy(file, Path.Combine(output, Path.GetFileName(file)));
            }
        }

        return workspace;
    }

    private sealed class TempWorkspace : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "workspace-" + Guid.NewGuid().ToString("N"));

        public TempWorkspace() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private EfCoreMcpTools CreateTools(bool connections = true, IToolSurfaceHints? hints = null)
    {
        var settings = new Dictionary<string, string?>();
        if (connections)
        {
            settings[$"Connections:{CliTestHost.ConnectionName}:ConnectionString"] = _host.Database.ConnectionString;
            settings[$"Connections:{CliTestHost.ConnectionName}:Provider"] = "Sqlite";
            settings[$"Connections:{CliTestHost.ConnectionName}:Environment"] = "Development";
            settings[$"Connections:{CliTestHost.ConnectionName}:AccessPolicy:AllowContexts:0"] = "SampleApp.SampleAppDbContext";
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var options = new QueryExecutionOptions { OutOfProcessHostPath = FixturePaths.QueryHostDllPath };
        var rawSqlOptions = new RawSqlExecutionOptions();
        var migrationsOptions = new MigrationsOptions();

        return new EfCoreMcpTools(
            new AssemblyLoaderService(),
            new AssemblyDiscoveryService(),
            new ConnectionRegistry(configuration),
            new SchemaCache(),
            new RoslynQueryExecutor(options, new QueryCompiler(new QueryCompilationOptions())),
            new OutOfProcessRoslynQueryExecutor(options),
            new PooledOutOfProcessRoslynQueryExecutor(new QueryHostPool(options, new OutOfProcessRoslynQueryExecutor(options), NullLogger<QueryHostPool>.Instance)),
            options,
            rawSqlOptions,
            new SqlQueryExecutor(rawSqlOptions, NullLogger<SqlQueryExecutor>.Instance),
            migrationsOptions,
            new MigrationInspector(migrationsOptions, NullLogger<MigrationInspector>.Instance),
            new ToonToolResultFormatter(),
            new ToolDiagnosticsOptions(),
            NullLogger<EfCoreMcpTools>.Instance,
            new EntityMutationsOptions(),
            new EntityMutationExecutor(NullLogger<EntityMutationExecutor>.Instance),
            NullMcpMetrics.Instance,
            surfaceHints: hints);
    }
}
