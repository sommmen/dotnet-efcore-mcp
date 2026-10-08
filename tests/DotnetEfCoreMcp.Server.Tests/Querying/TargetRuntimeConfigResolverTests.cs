using System.Text.Json;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.Querying;

/// <summary>Covers runtime-config resolution for the targets that do not ship their own
/// <c>*.runtimeconfig.json</c> - class libraries and dependency copies inside another app's output
/// folder - which previously could only be queried in-process.</summary>
public sealed class TargetRuntimeConfigResolverTests : IDisposable
{
    private readonly List<string> _temporaryDirectories = [];

    public void Dispose()
    {
        foreach (var directory in _temporaryDirectories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                // A leftover temp directory must never fail the test run.
            }
        }
    }

    [Fact]
    public void Resolve_TargetWithAdjacentRuntimeConfig_UsesItVerbatim()
    {
        var resolved = TargetRuntimeConfigResolver.Resolve(FixturePaths.SampleAppDllPath);

        Assert.Equal(Path.ChangeExtension(FixturePaths.SampleAppDllPath, ".runtimeconfig.json"), resolved);
    }

    [Fact]
    public void Resolve_ClassLibraryWithoutRuntimeConfig_SynthesizesOneFromRestoreGraph()
    {
        Assert.False(File.Exists(Path.ChangeExtension(FixturePaths.PackageDependencyAppDllPath, ".runtimeconfig.json")),
            "The fixture must stay a plain class library for this regression to mean anything.");

        var resolved = TargetRuntimeConfigResolver.Resolve(FixturePaths.PackageDependencyAppDllPath);

        Assert.True(File.Exists(resolved));
        var frameworks = ReadFrameworkNames(resolved);

        // The fixture carries a FrameworkReference to ASP.NET Core, which only the restore graph
        // records for a library - this is exactly what the adjacent-file check used to miss.
        Assert.Contains("Microsoft.AspNetCore.App", frameworks);
    }

    [Fact]
    public void Resolve_SynthesizedConfig_WritesThreePartFrameworkVersions()
    {
        // The restore-graph fallback derives a two-part version from the target framework moniker
        // ("net10.0" -> "10.0"), which hostfxr refuses to launch against.
        var resolved = TargetRuntimeConfigResolver.Resolve(FixturePaths.PackageDependencyAppDllPath);

        using var document = JsonDocument.Parse(File.ReadAllText(resolved));
        foreach (var framework in document.RootElement.GetProperty("runtimeOptions").GetProperty("frameworks").EnumerateArray())
        {
            var version = framework.GetProperty("version").GetString();
            Assert.Equal(3, Assert.IsType<Version>(Version.Parse(version!)).ToString().Split('.').Length);
        }
    }

    [Fact]
    public void Resolve_ClassLibraryWithoutRuntimeConfig_ReusesTheSameSynthesizedFile()
    {
        var first = TargetRuntimeConfigResolver.Resolve(FixturePaths.PackageDependencyAppDllPath);
        var second = TargetRuntimeConfigResolver.Resolve(FixturePaths.PackageDependencyAppDllPath);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Resolve_DependencyCopyInsideAnotherAppsOutput_UsesTheOwningAppsFramework()
    {
        // Reproduces the reported shape: the DbContext assembly exists only as a dependency copy in
        // some host application's bin folder, so it has no runtime config and no restore graph of
        // its own - only the owning app's runtime config describes the runtime.
        var directory = CreateTemporaryDirectory();
        var targetPath = Path.Combine(directory, "Acme.Dal.dll");
        File.WriteAllBytes(targetPath, File.ReadAllBytes(FixturePaths.SampleAppDllPath));
        File.WriteAllText(Path.Combine(directory, "HostApp.runtimeconfig.json"), """
            {
              "runtimeOptions": {
                "tfm": "net10.0",
                "frameworks": [
                  { "name": "Microsoft.NETCore.App", "version": "10.0.0" },
                  { "name": "Microsoft.AspNetCore.App", "version": "10.0.0" }
                ]
              }
            }
            """);

        var resolved = TargetRuntimeConfigResolver.Resolve(targetPath);

        var frameworks = ReadFrameworkNames(resolved);
        Assert.Contains("Microsoft.NETCore.App", frameworks);
        Assert.Contains("Microsoft.AspNetCore.App", frameworks);
    }

    [Fact]
    public void Resolve_TargetWithNoFrameworkInformationAtAll_FallsBackToTheRunningRuntime()
    {
        var directory = CreateTemporaryDirectory();
        var targetPath = Path.Combine(directory, "Orphan.dll");
        File.WriteAllBytes(targetPath, File.ReadAllBytes(FixturePaths.SampleAppDllPath));

        var resolved = TargetRuntimeConfigResolver.Resolve(targetPath);

        Assert.Equal(["Microsoft.NETCore.App"], ReadFrameworkNames(resolved));
        Assert.Equal($"net{Environment.Version.Major}.{Environment.Version.Minor}", ReadTargetFrameworkMoniker(resolved));
    }

    [Fact]
    public void Resolve_SelfContainedAppRuntimeConfig_SynthesizesAFrameworkDependentOne()
    {
        // A self-contained app's runtime config records includedFrameworks instead of a framework
        // reference, which `dotnet exec` cannot launch against; synthesis must take over.
        var directory = CreateTemporaryDirectory();
        var targetPath = Path.Combine(directory, "SelfContained.dll");
        File.WriteAllBytes(targetPath, File.ReadAllBytes(FixturePaths.SampleAppDllPath));
        File.WriteAllText(Path.ChangeExtension(targetPath, ".runtimeconfig.json"), """
            {
              "runtimeOptions": {
                "tfm": "net10.0",
                "includedFrameworks": [ { "name": "Microsoft.NETCore.App", "version": "10.0.0" } ]
              }
            }
            """);

        var resolved = TargetRuntimeConfigResolver.Resolve(targetPath);

        Assert.NotEqual(Path.ChangeExtension(targetPath, ".runtimeconfig.json"), resolved);
        Assert.Contains("Microsoft.NETCore.App", ReadFrameworkNames(resolved));
    }

    private static List<string> ReadFrameworkNames(string runtimeConfigPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(runtimeConfigPath));
        var runtimeOptions = document.RootElement.GetProperty("runtimeOptions");
        var names = new List<string>();

        if (runtimeOptions.TryGetProperty("framework", out var single))
        {
            names.Add(single.GetProperty("name").GetString()!);
        }

        if (runtimeOptions.TryGetProperty("frameworks", out var many))
        {
            names.AddRange(many.EnumerateArray().Select(element => element.GetProperty("name").GetString()!));
        }

        return names;
    }

    private static string? ReadTargetFrameworkMoniker(string runtimeConfigPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(runtimeConfigPath));
        return document.RootElement.GetProperty("runtimeOptions").GetProperty("tfm").GetString();
    }

    private string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "efcore-mcp-runtimeconfig-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        _temporaryDirectories.Add(directory);
        return directory;
    }
}
