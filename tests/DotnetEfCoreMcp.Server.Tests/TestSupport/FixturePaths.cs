namespace DotnetEfCoreMcp.Server.Tests.TestSupport;

/// <summary>Resolves paths to separately-built fixture assemblies. The fixtures are intentionally
/// not compile-time project references so tests exercise the real compiled-DLL loading path.</summary>
public static class FixturePaths
{
    public static string SampleAppDllPath => GetFixtureDllPath("SampleApp", "SampleApp.dll");

    public static string NoContextAppDllPath => GetFixtureDllPath("NoContextApp", "NoContextApp.dll");

    public static string BrokenDependencyAppDllPath => GetFixtureDllPath("BrokenDependencyApp", "BrokenDependencyApp.dll");

    public static string PackageDependencyAppDllPath => GetFixtureDllPath("PackageDependencyApp", "PackageDependencyApp.dll");

    public static string ManyDbSetsAppDllPath => GetFixtureDllPath("ManyDbSetsApp", "ManyDbSetsApp.dll");

    public static string IdentityAppDllPath => GetFixtureDllPath("IdentityApp", "IdentityApp.dll");

    /// <summary>The DbContext-only half of the split-assembly fixture pair: contains
    /// <c>SplitContextApp.SplitDbContext</c> but no migrations.</summary>
    public static string SplitContextAppDllPath => GetFixtureDllPath("SplitContextApp", "SplitContextApp.dll");

    /// <summary>The migrations-only half of the split-assembly fixture pair: contains an EF Core
    /// migration for <c>SplitContextApp.SplitDbContext</c> (via ProjectReference) but no DbContext
    /// of its own, simulating scenarios like <c>AuthDbContext</c> in <c>OPG.DAL</c> with migrations
    /// in <c>OPG.AuthApi</c>.</summary>
    public static string SplitMigrationsAppDllPath => GetFixtureDllPath("SplitMigrationsApp", "SplitMigrationsApp.dll");

    private static string GetFixtureDllPath(string fixtureName, string assemblyFileName)
    {
        foreach (var config in new[] { "Debug", "Release" })
        {
            var candidate = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..",
                "Fixtures", fixtureName, "bin", config, "net10.0", assemblyFileName));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"{fixtureName} fixture DLL not found under tests/Fixtures/{fixtureName}/bin/{{Debug,Release}}/net10.0/{assemblyFileName}. " +
            "It should be built automatically by the test project's BuildFixtureAssemblies pre-build target.");
    }
}
