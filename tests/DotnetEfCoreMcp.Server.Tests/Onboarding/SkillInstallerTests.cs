using DotnetEfCoreMcp.Server.Onboarding;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.Onboarding;

/// <summary>
/// Covers installing, updating, and removing skill folders, and the refusal to clobber a directory
/// this tool did not create.
/// </summary>
[Collection(OnboardingEnvironmentCollection.Name)]
public class SkillInstallerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "efcore-skill-install-tests", Guid.NewGuid().ToString("n"));

    private readonly string _source;
    private readonly string _repository;
    private readonly string? _previousHome;

    public SkillInstallerTests()
    {
        _source = Path.Combine(_root, "source");
        _repository = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repository);

        _previousHome = Environment.GetEnvironmentVariable(UserDirectory.OverrideVariable);

        // User-scoped installs must land in the test's own directory, never the real profile.
        Environment.SetEnvironmentVariable(UserDirectory.OverrideVariable, Path.Combine(_root, "home"));

        WriteSkill("sample-skill");
    }

    [Fact]
    public void Install_CopiesSkillIntoWorkspaceDirectory()
    {
        var results = CreateInstaller().Install(InstallScope.Workspace, _repository);

        var result = Assert.Single(results);
        Assert.True(result.Changed);
        Assert.Equal(
            Path.Combine(_repository, ".github", "skills", "sample-skill"),
            result.Path);
        Assert.True(File.Exists(Path.Combine(result.Path, "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(result.Path, "references", "guide.md")));
    }

    [Fact]
    public void Install_UserScopeWritesUnderHomeDirectory()
    {
        var results = CreateInstaller().Install(InstallScope.User, _repository);

        var result = Assert.Single(results);
        Assert.True(result.Changed);
        Assert.Equal(
            Path.Combine(_root, "home", ".agents", "skills", "sample-skill"),
            result.Path);

        // A user-scoped install must not also touch the repository.
        Assert.False(Directory.Exists(Path.Combine(_repository, ".github", "skills")));
    }

    [Fact]
    public void Install_ExcludesEvalScaffolding()
    {
        var results = CreateInstaller().Install(InstallScope.Workspace, _repository);

        // evals/ is skill-development material; shipping it to a user is noise.
        Assert.False(Directory.Exists(Path.Combine(Assert.Single(results).Path, "evals")));
    }

    [Fact]
    public void Install_IsIdempotent()
    {
        var installer = CreateInstaller();
        installer.Install(InstallScope.Workspace, _repository);

        var second = Assert.Single(installer.Install(InstallScope.Workspace, _repository));

        Assert.False(second.Changed);
        Assert.Contains("Already up to date", second.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_RefreshesAnOutdatedInstall()
    {
        var installer = CreateInstaller();
        var destination = Assert.Single(installer.Install(InstallScope.Workspace, _repository)).Path;

        File.WriteAllText(Path.Combine(destination, "SKILL.md"), "stale content");

        var refreshed = Assert.Single(installer.Install(InstallScope.Workspace, _repository));

        Assert.True(refreshed.Changed);
        Assert.Contains("Updated", refreshed.Message, StringComparison.Ordinal);
        Assert.Contains("sample skill", File.ReadAllText(Path.Combine(destination, "SKILL.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_RefusesToReplaceAnUnmanagedDirectory()
    {
        // Someone's hand-written skill of the same name must survive: silently overwriting it would
        // be data loss, not an upgrade.
        var destination = Path.Combine(_repository, ".github", "skills", "sample-skill");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "SKILL.md"), "hand written");

        var result = Assert.Single(CreateInstaller().Install(InstallScope.Workspace, _repository));

        Assert.False(result.Changed);
        Assert.Contains("not installed by dotnet-efcore-mcp", result.Message, StringComparison.Ordinal);
        Assert.Equal("hand written", File.ReadAllText(Path.Combine(destination, "SKILL.md")));
    }

    [Fact]
    public void Uninstall_RemovesOnlyManagedInstalls()
    {
        var installer = CreateInstaller();
        var destination = Assert.Single(installer.Install(InstallScope.Workspace, _repository)).Path;

        var removed = Assert.Single(installer.Uninstall(InstallScope.Workspace, _repository));
        Assert.True(removed.Changed);
        Assert.False(Directory.Exists(destination));

        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "SKILL.md"), "hand written");

        var skipped = Assert.Single(installer.Uninstall(InstallScope.Workspace, _repository));
        Assert.False(skipped.Changed);
        Assert.True(File.Exists(Path.Combine(destination, "SKILL.md")));
    }

    [Fact]
    public void Status_ReportsInstallState()
    {
        var installer = CreateInstaller();

        Assert.Equal("not installed",
            Assert.Single(installer.Status(InstallScope.Workspace, _repository)).Message);

        installer.Install(InstallScope.Workspace, _repository);

        Assert.Equal("installed",
            Assert.Single(installer.Status(InstallScope.Workspace, _repository)).Message);
    }

    [Fact]
    public void Install_ReportsUnknownSkillNames()
    {
        var result = Assert.Single(CreateInstaller().Install(InstallScope.Workspace, _repository, "no-such-skill"));

        Assert.False(result.Changed);
        Assert.Contains("Unknown skill", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_ReportsWhenNoSkillsAreBundled()
    {
        // A build without the packaged skills folder must say so rather than silently succeeding.
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        var result = Assert.Single(new SkillInstaller(empty).Install(InstallScope.Workspace, _repository));

        Assert.False(result.Changed);
        Assert.Contains("No skills are bundled", result.Message, StringComparison.Ordinal);
    }

    private SkillInstaller CreateInstaller() => new(_source);

    private void WriteSkill(string name)
    {
        var skill = Path.Combine(_source, name);
        Directory.CreateDirectory(Path.Combine(skill, "references"));
        Directory.CreateDirectory(Path.Combine(skill, "evals"));

        File.WriteAllText(Path.Combine(skill, "SKILL.md"), $"---\nname: {name}\n---\n\nA sample skill body.\n");
        File.WriteAllText(Path.Combine(skill, "references", "guide.md"), "reference material\n");
        File.WriteAllText(Path.Combine(skill, "evals", "evals.json"), "{}\n");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        Environment.SetEnvironmentVariable(UserDirectory.OverrideVariable, _previousHome);

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
