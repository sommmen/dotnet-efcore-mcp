namespace DotnetEfCoreMcp.Server.Tests.TestSupport;

/// <summary>Resolves the path to the SampleApp test fixture's build output. The fixture is
/// intentionally not a compile-time ProjectReference (see the test project's csproj comment) so
/// that tests exercise the real "load a compiled DLL from disk" flow the server uses for actual
/// target projects. The test project's csproj has a pre-build target that runs `dotnet build` on
/// the fixture, so its output should always be present by the time tests run.</summary>
public static class FixturePaths
{
    public static string SampleAppDllPath
    {
        get
        {
            // Test output: tests/DotnetEfCoreMcp.Server.Tests/bin/<Config>/net10.0/
            // Fixture output: tests/Fixtures/SampleApp/bin/<Config>/net10.0/SampleApp.dll
            // From the test output directory that's four levels up to `tests/`, then down into
            // Fixtures/SampleApp/bin/<Config>/net10.0. Try both configurations since the fixture
            // is always built with the same -c as the test run, but this keeps the helper robust
            // either way.
            foreach (var config in new[] { "Debug", "Release" })
            {
                var candidate = Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory, "..", "..", "..", "..",
                    "Fixtures", "SampleApp", "bin", config, "net10.0", "SampleApp.dll"));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException(
                "SampleApp fixture DLL not found under tests/Fixtures/SampleApp/bin/{Debug,Release}/net10.0/SampleApp.dll. " +
                "It should be built automatically by the test project's BuildSampleAppFixture pre-build target - " +
                "try `dotnet build` on the fixture project directly (tests/Fixtures/SampleApp) to diagnose.");
        }
    }
}
