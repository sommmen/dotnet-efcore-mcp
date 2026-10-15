using DotnetEfCoreMcp.Server.Hooks;

namespace DotnetEfCoreMcp.Server.Tests.Hooks;

/// <summary>
/// Covers the heuristic that keeps a user-scoped session-start hook quiet in repositories that have
/// nothing to do with EF Core.
/// </summary>
public class EfCoreWorkspaceProbeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "efcore-probe-tests", Guid.NewGuid().ToString("n"));

    public EfCoreWorkspaceProbeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void LooksLikeEfCoreWorkspace_DetectsPackageReferenceInRootProject()
    {
        Write("App.csproj", """<Project><ItemGroup><PackageReference Include="Microsoft.EntityFrameworkCore" /></ItemGroup></Project>""");

        Assert.True(EfCoreWorkspaceProbe.LooksLikeEfCoreWorkspace(_root));
    }

    [Fact]
    public void LooksLikeEfCoreWorkspace_DetectsNestedProjectInConventionalLayout()
    {
        Write(Path.Combine("src", "App.Data", "App.Data.csproj"),
            """<Project><ItemGroup><PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" /></ItemGroup></Project>""");

        Assert.True(EfCoreWorkspaceProbe.LooksLikeEfCoreWorkspace(_root));
    }

    [Fact]
    public void LooksLikeEfCoreWorkspace_DetectsCentralPackageManagement()
    {
        // With CPM the version - and often the only textual reference - lives here, not in the csproj.
        Write("Directory.Packages.props",
            """<Project><ItemGroup><PackageVersion Include="Microsoft.EntityFrameworkCore" Version="9.0.0" /></ItemGroup></Project>""");

        Assert.True(EfCoreWorkspaceProbe.LooksLikeEfCoreWorkspace(_root));
    }

    [Fact]
    public void LooksLikeEfCoreWorkspace_IgnoresNonEfCoreProjects()
    {
        Write("App.csproj", """<Project><ItemGroup><PackageReference Include="Serilog" /></ItemGroup></Project>""");
        Write("package.json", """{ "name": "frontend" }""");

        Assert.False(EfCoreWorkspaceProbe.LooksLikeEfCoreWorkspace(_root));
    }

    [Fact]
    public void LooksLikeEfCoreWorkspace_IgnoresBuildOutput()
    {
        // A stale EF Core reference in bin/obj must not keep announcing in a project that dropped it.
        Write(Path.Combine("obj", "project.assets.json"), """{ "Microsoft.EntityFrameworkCore": {} }""");
        Write(Path.Combine("bin", "App.deps.json"), """{ "Microsoft.EntityFrameworkCore": {} }""");

        Assert.False(EfCoreWorkspaceProbe.LooksLikeEfCoreWorkspace(_root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LooksLikeEfCoreWorkspace_HandlesMissingWorkspace(string? workspace) =>
        Assert.False(EfCoreWorkspaceProbe.LooksLikeEfCoreWorkspace(workspace));

    [Fact]
    public void LooksLikeEfCoreWorkspace_HandlesNonExistentDirectory() =>
        Assert.False(EfCoreWorkspaceProbe.LooksLikeEfCoreWorkspace(Path.Combine(_root, "missing")));

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Leftover temp state is harmless.
        }
    }
}
