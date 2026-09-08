using DotnetEfCoreMcp.Server.Querying;

namespace DotnetEfCoreMcp.Server.Tests.Querying;

public sealed class QueryHostLocatorTests : IDisposable
{
    private readonly string _tempRoot = Directory.CreateTempSubdirectory("query-host-locator-tests-").FullName;

    public void Dispose() => Directory.Delete(_tempRoot, recursive: true);

    [Fact]
    public void TryGetBundledHostPath_ReturnsBundledPath_WhenQueryHostSubfolderPresent()
    {
        var queryHostDir = Path.Combine(_tempRoot, "queryhost");
        Directory.CreateDirectory(queryHostDir);
        var expected = Path.Combine(queryHostDir, "DotnetEfCoreMcp.QueryHost.dll");
        File.WriteAllText(expected, "");
        var depsFile = Path.ChangeExtension(expected, ".deps.json");
        File.WriteAllText(depsFile, "");

        var result = QueryHostLocator.TryGetBundledHostPath(_tempRoot);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("Debug")]
    [InlineData("Release")]
    public void TryGetBundledHostPath_FallsBackToSiblingProjectBuildOutput_WhenNoBundledSubfolderPresent(string configuration)
    {
        // Nest the "base directory" four levels under the isolated temp root (mirroring
        // <repo>/src/DotnetEfCoreMcp.Server/bin/<config>/net10.0/) so the "../../../../" fallback
        // traversal stays entirely inside the disposable temp directory instead of escaping into
        // real ancestor folders.
        var baseDirectory = Path.Combine(_tempRoot, "src", "DotnetEfCoreMcp.Server", "bin", configuration, "net10.0");
        Directory.CreateDirectory(baseDirectory);
        var siblingBinDir = Path.Combine(_tempRoot, "src", "DotnetEfCoreMcp.QueryHost", "bin", configuration, "net10.0");
        Directory.CreateDirectory(siblingBinDir);
        var expected = Path.Combine(siblingBinDir, "DotnetEfCoreMcp.QueryHost.dll");
        File.WriteAllText(expected, "");
        var depsFile = Path.ChangeExtension(expected, ".deps.json");
        File.WriteAllText(depsFile, "");

        var result = QueryHostLocator.TryGetBundledHostPath(baseDirectory);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void TryGetBundledHostPath_ReturnsNull_WhenNoQueryHostFound()
    {
        var result = QueryHostLocator.TryGetBundledHostPath(_tempRoot);

        Assert.Null(result);
    }
}
