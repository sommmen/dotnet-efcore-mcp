using DotnetEfCoreMcp.Server.Updates;

namespace DotnetEfCoreMcp.Server.Tests.Updates;

/// <summary>
/// Covers install-method detection, which decides whether an update notice is shown at all and
/// which upgrade command it names.
/// </summary>
/// <remarks>
/// The detector is constructed with explicit paths and an explicit environment rather than reading
/// the real process, so these tests describe synthetic layouts instead of depending on how the
/// developer's machine happens to have this tool installed.
/// </remarks>
public class InstallMethodDetectorTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "efcore-install-detect", Guid.NewGuid().ToString("n"));

    private static InstallMethodDetector Create(
        string baseDirectory,
        string? workingDirectory = null,
        IDictionary<string, string?>? environment = null)
    {
        var variables = environment ?? new Dictionary<string, string?>();

        return new InstallMethodDetector(
            baseDirectory,
            workingDirectory ?? Path.GetTempPath(),
            name => variables.TryGetValue(name, out var value) ? value : null);
    }

    [Fact]
    public void Detects_global_tool_from_store_layout()
    {
        var path = Path.Combine("C:", "Users", "dev", ".dotnet", "tools", ".store",
            "dotnetefcoremcp.server", "0.1.40-preview", "tools", "net10.0", "any");

        Assert.Equal(InstallMethod.GlobalTool, Create(path).Detect());
    }

    [Fact]
    public void Detects_dnx_from_package_cache_layout()
    {
        // dnx runs the tool straight out of the NuGet package cache rather than unpacking it into
        // a .store directory, which is what distinguishes it from an installed tool.
        var path = Path.Combine("C:", "Users", "dev", ".nuget", "packages",
            "dotnetefcoremcp.server", "0.1.40-preview", "tools", "net10.0", "any");

        Assert.Equal(InstallMethod.Dnx, Create(path).Detect());
    }

    [Fact]
    public void Detects_npm_from_the_variable_the_wrapper_sets()
    {
        var environment = new Dictionary<string, string?>
        {
            [InstallMethodDetector.InstallMethodVariable] = "npm",
        };

        // Deliberately paired with a global-tool path: the npm wrapper installs the .NET tool
        // globally, so without the variable winning, an npm launch would be misread as a plain
        // global install and the user would be told to run a command the wrapper already runs.
        var path = Path.Combine("C:", "Users", "dev", ".dotnet", "tools", ".store",
            "dotnetefcoremcp.server", "0.1.40-preview", "tools");

        Assert.Equal(InstallMethod.Npm, Create(path, environment: environment).Detect());
    }

    [Fact]
    public void Treats_an_unrecognized_declared_method_as_unknown()
    {
        var environment = new Dictionary<string, string?>
        {
            [InstallMethodDetector.InstallMethodVariable] = "something-we-do-not-ship",
        };

        var path = Path.Combine("C:", "Users", "dev", ".dotnet", "tools", ".store",
            "dotnetefcoremcp.server", "0.1.40-preview");

        Assert.Equal(InstallMethod.Unknown, Create(path, environment: environment).Detect());
    }

    [Fact]
    public void Detects_local_tool_when_a_manifest_names_this_package()
    {
        var workspace = Path.Combine(_root, "repo", "src", "nested");
        Directory.CreateDirectory(workspace);

        var config = Path.Combine(_root, "repo", ".config");
        Directory.CreateDirectory(config);
        File.WriteAllText(
            Path.Combine(config, InstallMethodDetector.ManifestFileName),
            """{"tools":{"dotnetefcoremcp.server":{"version":"0.1.40-preview"}}}""");

        var path = Path.Combine("C:", "Users", "dev", ".dotnet", "tools", ".store",
            "dotnetefcoremcp.server", "0.1.40-preview");

        // Found by walking up from a nested directory, matching how the .NET CLI locates manifests.
        Assert.Equal(InstallMethod.LocalTool, Create(path, workspace).Detect());
    }

    [Fact]
    public void Ignores_a_manifest_that_does_not_mention_this_package()
    {
        var workspace = Path.Combine(_root, "other", "src");
        Directory.CreateDirectory(workspace);

        var config = Path.Combine(_root, "other", ".config");
        Directory.CreateDirectory(config);
        File.WriteAllText(
            Path.Combine(config, InstallMethodDetector.ManifestFileName),
            """{"tools":{"dotnet-ef":{"version":"10.0.0"}}}""");

        var path = Path.Combine("C:", "Users", "dev", ".dotnet", "tools", ".store",
            "dotnetefcoremcp.server", "0.1.40-preview");

        // An unrelated manifest in a parent directory must not make a global install look local,
        // or the user gets told to update a manifest that never listed this tool.
        Assert.Equal(InstallMethod.GlobalTool, Create(path, workspace).Detect());
    }

    [Fact]
    public void Returns_unknown_for_an_ordinary_build_output_path()
    {
        var path = Path.Combine("C:", "Repos", "my-app", "src", "Server", "bin", "Debug", "net10.0");

        Assert.Equal(InstallMethod.Unknown, Create(path).Detect());
    }

    [Fact]
    public void Returns_unknown_for_a_directory_merely_named_after_the_package()
    {
        // Guards the narrowness of the package-cache rule: the id must sit directly under a
        // `packages` directory, not merely appear somewhere in the path.
        var path = Path.Combine("C:", "Repos", "dotnetefcoremcp.server", "bin", "Debug");

        Assert.Equal(InstallMethod.Unknown, Create(path).Detect());
    }

    [Fact]
    public void Returns_unknown_for_an_empty_path()
    {
        Assert.Equal(InstallMethod.Unknown, Create(string.Empty).Detect());
    }

    [Theory]
    [InlineData(InstallMethod.GlobalTool, "dotnet tool update --global DotnetEfCoreMcp.Server --prerelease")]
    [InlineData(InstallMethod.LocalTool, "dotnet tool update --local DotnetEfCoreMcp.Server --prerelease")]
    public void Names_the_upgrade_command_for_notifiable_methods(InstallMethod method, string expected)
    {
        Assert.Equal(expected, InstallMethodDetector.GetUpgradeCommand(method, includePrerelease: true));
    }

    [Fact]
    public void Omits_the_prerelease_flag_for_a_stable_target()
    {
        Assert.Equal(
            "dotnet tool update --global DotnetEfCoreMcp.Server",
            InstallMethodDetector.GetUpgradeCommand(InstallMethod.GlobalTool, includePrerelease: false));
    }

    [Theory]
    [InlineData(InstallMethod.Dnx)]
    [InlineData(InstallMethod.Npm)]
    [InlineData(InstallMethod.Unknown)]
    public void Suggests_nothing_for_self_updating_or_unnameable_methods(InstallMethod method)
    {
        // dnx refetches per run and the npm wrapper updates before launch, so both are already
        // current; unknown cannot be named safely. None of the three should produce a notice.
        Assert.Null(InstallMethodDetector.GetUpgradeCommand(method, includePrerelease: true));
        Assert.False(InstallMethodDetector.ShouldNotify(method));
    }

    [Theory]
    [InlineData(InstallMethod.GlobalTool)]
    [InlineData(InstallMethod.LocalTool)]
    public void Notifies_only_for_methods_with_a_real_upgrade_command(InstallMethod method)
    {
        Assert.True(InstallMethodDetector.ShouldNotify(method));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
