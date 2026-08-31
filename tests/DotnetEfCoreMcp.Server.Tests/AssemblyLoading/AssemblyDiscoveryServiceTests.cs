using DotnetEfCoreMcp.Server.AssemblyLoading;

namespace DotnetEfCoreMcp.Server.Tests.AssemblyLoading;

public sealed class AssemblyDiscoveryServiceTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"efcore-mcp-discovery-{Guid.NewGuid():N}");
    private readonly AssemblyDiscoveryService _service = new();

    public AssemblyDiscoveryServiceTests() => Directory.CreateDirectory(_workspace);

    [Fact]
    public void Discover_PrefersDebugAndReturnsOnlyProjectOutputAssemblies()
    {
        WriteProject("src/App/App.csproj", "App.Output");
        WriteDll("src/App/bin/Release/net9.0/App.Output.dll", new DateTime(2026, 1, 3));
        WriteDll("src/App/bin/Debug/net8.0/App.Output.dll", new DateTime(2026, 1, 1));
        WriteDll("src/App/bin/Debug/net8.0/Dependency.dll", new DateTime(2026, 1, 4));
        WriteDll("src/App/bin/Debug/net8.0/ref/App.Output.dll", new DateTime(2026, 1, 5));

        var candidates = _service.Discover(_workspace);

        Assert.Equal(2, candidates.Count);
        Assert.Equal("Debug", candidates[0].Configuration);
        Assert.True(candidates[0].IsPreferred);
        Assert.Equal("Release", candidates[1].Configuration);
        Assert.False(candidates[1].IsPreferred);
        Assert.All(candidates, candidate => Assert.Equal("App.Output.dll", Path.GetFileName(candidate.AssemblyPath)));
    }

    [Fact]
    public void Discover_UsesNewestDebugOutputAcrossProjectsThenHighestTargetFramework()
    {
        WriteProject("First/First.csproj");
        WriteProject("Second/Second.csproj");
        WriteDll("First/bin/Debug/net8.0/First.dll", new DateTime(2026, 1, 1));
        WriteDll("Second/bin/Debug/net8.0/Second.dll", new DateTime(2026, 1, 2));
        WriteDll("Second/bin/Debug/net10.0/Second.dll", new DateTime(2026, 1, 2));

        var candidates = _service.Discover(_workspace);

        Assert.EndsWith(Path.Combine("Second", "bin", "Debug", "net10.0", "Second.dll"), candidates[0].AssemblyPath);
        Assert.EndsWith(Path.Combine("Second", "bin", "Debug", "net8.0", "Second.dll"), candidates[1].AssemblyPath);
    }

    [Fact]
    public void Discover_ReturnsEmptyWhenProjectsHaveNotBeenBuilt()
    {
        WriteProject("App/App.csproj");

        Assert.Empty(_service.Discover(_workspace));
    }

    [Fact]
    public void Discover_RejectsMissingWorkspace()
    {
        Directory.Delete(_workspace, recursive: true);

        var exception = Assert.Throws<AssemblyDiscoveryException>(() => _service.Discover(_workspace));

        Assert.Contains("does not exist", exception.Message);
    }

    private void WriteProject(string relativePath, string? assemblyName = null)
    {
        var path = Path.Combine(_workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, assemblyName is null
            ? "<Project Sdk=\"Microsoft.NET.Sdk\" />"
            : $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>{assemblyName}</AssemblyName></PropertyGroup></Project>");
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
