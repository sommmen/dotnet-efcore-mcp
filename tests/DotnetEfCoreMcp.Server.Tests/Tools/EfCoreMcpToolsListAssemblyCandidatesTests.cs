using System.Text.Json;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.Migrations;
using DotnetEfCoreMcp.Server.Mutations;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Schema;
using DotnetEfCoreMcp.Server.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetEfCoreMcp.Server.Tests.Tools;

/// <summary>Covers the monorepo ergonomics improvements to <c>list_assembly_candidates</c>:
/// grouping multiple builds of the same project into one representative entry by default,
/// exposing an opt-in <c>includeAllBuilds</c> escape hatch, and an optional <c>pathFilter</c> so
/// callers can narrow results without a separate grep step.</summary>
public sealed class EfCoreMcpToolsListAssemblyCandidatesTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"efcore-mcp-list-candidates-{Guid.NewGuid():N}");

    public EfCoreMcpToolsListAssemblyCandidatesTests() => Directory.CreateDirectory(_workspace);

    [Fact]
    public void ListAssemblyCandidates_ByDefault_GroupsMultipleBuildsPerProject()
    {
        WriteProject("Data/Data.csproj");
        WriteDll("Data/bin/Debug/net8.0/Data.dll", new DateTime(2026, 1, 1));
        WriteDll("Data/bin/Debug/net10.0/Data.dll", new DateTime(2026, 1, 1));
        WriteDll("Data/bin/Release/net8.0/Data.dll", new DateTime(2026, 1, 1));

        var tools = CreateTools();

        using var document = JsonDocument.Parse(tools.ListAssemblyCandidates(_workspace));
        var root = document.RootElement;

        var candidates = root.GetProperty("candidates").EnumerateArray().ToArray();
        Assert.Single(candidates);
        Assert.Equal(2, candidates[0].GetProperty("otherBuildsOfThisProject").GetInt32());
        Assert.Equal(3, root.GetProperty("totalCandidateCount").GetInt32());
    }

    [Fact]
    public void ListAssemblyCandidates_WithIncludeAllBuilds_ListsEveryBuildIndividually()
    {
        WriteProject("Data/Data.csproj");
        WriteDll("Data/bin/Debug/net8.0/Data.dll", new DateTime(2026, 1, 1));
        WriteDll("Data/bin/Release/net8.0/Data.dll", new DateTime(2026, 1, 1));

        var tools = CreateTools();

        using var document = JsonDocument.Parse(tools.ListAssemblyCandidates(_workspace, includeAllBuilds: true));
        var root = document.RootElement;

        Assert.Equal(2, root.GetProperty("candidates").EnumerateArray().Count());
    }

    [Fact]
    public void ListAssemblyCandidates_WithPathFilter_OnlyReturnsMatchingProjects()
    {
        WriteProject("Northwind/Northwind.csproj");
        WriteProject("Tools/Tools.csproj");
        WriteDll("Northwind/bin/Debug/net8.0/Northwind.dll", new DateTime(2026, 1, 1));
        WriteDll("Tools/bin/Debug/net8.0/Tools.dll", new DateTime(2026, 1, 1));

        var tools = CreateTools();

        var json = tools.ListAssemblyCandidates(_workspace, pathFilter: "Northwind");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var candidates = root.GetProperty("candidates").EnumerateArray().ToArray();
        Assert.True(candidates.Length == 1, json);
        Assert.Contains("Northwind", candidates[0].GetProperty("projectPath").GetString());
    }

    private static EfCoreMcpTools CreateTools()
    {
        var configuration = new ConfigurationBuilder().Build();
        var rawSqlOptions = new RawSqlExecutionOptions();

        return new EfCoreMcpTools(
            new AssemblyLoaderService(),
            new AssemblyDiscoveryService(),
            new ConnectionRegistry(configuration),
            new SchemaCache(),
            new RoslynQueryExecutor(new QueryExecutionOptions(), new QueryCompiler(new QueryCompilationOptions())),
            new OutOfProcessRoslynQueryExecutor(new QueryExecutionOptions()),
            new QueryExecutionOptions(),
            rawSqlOptions,
            new SqlQueryExecutor(rawSqlOptions, NullLogger<SqlQueryExecutor>.Instance),
            new MigrationsOptions(),
            new MigrationInspector(new MigrationsOptions(), NullLogger<MigrationInspector>.Instance),
            new JsonToolResultFormatter(),
            new ToolDiagnosticsOptions(),
            NullLogger<EfCoreMcpTools>.Instance,
            new EntityMutationsOptions(),
            new EntityMutationExecutor(NullLogger<EntityMutationExecutor>.Instance));
    }

    private void WriteProject(string relativePath)
    {
        var path = Path.Combine(_workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
    }

    private void WriteDll(string relativePath, DateTime lastWriteTimeUtc)
    {
        var path = Path.Combine(_workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
        File.SetLastWriteTimeUtc(path, DateTime.SpecifyKind(lastWriteTimeUtc, DateTimeKind.Utc));
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }
}
