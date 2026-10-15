using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetEfCoreMcp.Server.Hooks;
using DotnetEfCoreMcp.Server.Onboarding;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.Onboarding;

/// <summary>
/// Covers user-scoped hook installation and the session-start entry, both added so the hooks are
/// discoverable without a per-repository opt-in.
/// </summary>
[Collection(OnboardingEnvironmentCollection.Name)]
public class HookInstallScopeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "efcore-hook-scope-tests", Guid.NewGuid().ToString("n"));

    private readonly string _repository;
    private readonly string _home;
    private readonly string? _previousHome;

    public HookInstallScopeTests()
    {
        _repository = Path.Combine(_root, "repo");
        _home = Path.Combine(_root, "home");
        Directory.CreateDirectory(_repository);
        Directory.CreateDirectory(_home);

        _previousHome = Environment.GetEnvironmentVariable(UserDirectory.OverrideVariable);
        Environment.SetEnvironmentVariable(UserDirectory.OverrideVariable, _home);
    }

    [Theory]
    [InlineData(HookClient.Claude, ".claude", "settings.json")]
    [InlineData(HookClient.Codex, ".codex", "hooks.json")]
    public void GetConfigPath_UserScopeResolvesUnderHome(HookClient client, string folder, string file) =>
        Assert.Equal(
            Path.Combine(_home, folder, file),
            HookInstaller.GetConfigPath(client, _repository, InstallScope.User));

    [Fact]
    public void GetConfigPath_CopilotUserScopeUsesItsOwnHomeDirectory() =>
        // Copilot's user hooks live under ~/.copilot/hooks, not alongside the repo's .github/hooks.
        Assert.Equal(
            Path.Combine(_home, ".copilot", "hooks", "dotnet-efcore-mcp.json"),
            HookInstaller.GetConfigPath(HookClient.Copilot, _repository, InstallScope.User));

    [Fact]
    public void GetConfigPath_DefaultsToWorkspaceScope() =>
        Assert.Equal(
            HookInstaller.GetConfigPath(HookClient.Claude, _repository, InstallScope.Workspace),
            HookInstaller.GetConfigPath(HookClient.Claude, _repository));

    [Theory]
    [InlineData(HookClient.Claude)]
    [InlineData(HookClient.Copilot)]
    [InlineData(HookClient.Codex)]
    public void Install_UserScopeLeavesTheRepositoryUntouched(HookClient client)
    {
        var result = new HookInstaller("1.2.3").Install(client, _repository, InstallScope.User);

        Assert.True(result.Changed);
        Assert.True(File.Exists(result.FilePath));
        Assert.StartsWith(_home, result.FilePath, StringComparison.Ordinal);
        Assert.False(File.Exists(HookInstaller.GetConfigPath(client, _repository, InstallScope.Workspace)));
    }

    [Theory]
    [InlineData(HookClient.Claude)]
    [InlineData(HookClient.Copilot)]
    [InlineData(HookClient.Codex)]
    public void Install_WritesASessionStartActivationHook(HookClient client)
    {
        var result = new HookInstaller("1.2.3").Install(client, _repository);

        var json = File.ReadAllText(result.FilePath);
        Assert.Contains("SessionStart", json, StringComparison.Ordinal);
        Assert.Contains("activate", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(InstallScope.Workspace, "workspace")]
    [InlineData(InstallScope.User, "user")]
    public void Install_PassesTheScopeToTheHookCommand(InstallScope scope, string expected)
    {
        // The hook has to know its own scope at runtime: a user-level hook fires everywhere and must
        // check the workspace before announcing, while a repo-local one is already an opt-in.
        var result = new HookInstaller("1.2.3").Install(HookClient.Claude, _repository, scope);

        Assert.Contains($"--scope {expected}", File.ReadAllText(result.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void Install_CopilotPassesScopeAsASeparateArgument()
    {
        // Copilot uses exec/args rather than a shell string, so the flag must be its own element.
        var result = new HookInstaller("1.2.3").Install(HookClient.Copilot, _repository, InstallScope.User);

        var root = JsonNode.Parse(File.ReadAllText(result.FilePath))!.AsObject();
        var args = root["hooks"]!["SessionStart"]!.AsArray()[0]!["args"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .ToList();

        Assert.Contains("--scope", args);
        Assert.Contains("user", args);
        Assert.Contains("activate", args);
    }

    [Fact]
    public void Install_UserScopePreservesUnrelatedUserSettings()
    {
        // ~/.claude/settings.json is the user's own file; clobbering it would be unacceptable.
        var path = HookInstaller.GetConfigPath(HookClient.Claude, _repository, InstallScope.User);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "theme": "dark" }""");

        new HookInstaller("1.2.3").Install(HookClient.Claude, _repository, InstallScope.User);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("dark", document.RootElement.GetProperty("theme").GetString());
        Assert.True(document.RootElement.TryGetProperty("hooks", out _));
    }

    [Fact]
    public void Status_DistinguishesTheTwoScopes()
    {
        var installer = new HookInstaller("1.2.3");
        installer.Install(HookClient.Claude, _repository, InstallScope.User);

        Assert.Equal("installed", installer.Status(HookClient.Claude, _repository, InstallScope.User).Message);
        Assert.Equal("not installed", installer.Status(HookClient.Claude, _repository, InstallScope.Workspace).Message);
    }

    [Fact]
    public void Uninstall_UserScopeRemovesOnlyTheUserCopy()
    {
        var installer = new HookInstaller("1.2.3");
        installer.Install(HookClient.Codex, _repository, InstallScope.User);
        installer.Install(HookClient.Codex, _repository, InstallScope.Workspace);

        installer.Uninstall(HookClient.Codex, _repository, InstallScope.User);

        Assert.False(File.Exists(HookInstaller.GetConfigPath(HookClient.Codex, _repository, InstallScope.User)));
        Assert.True(File.Exists(HookInstaller.GetConfigPath(HookClient.Codex, _repository, InstallScope.Workspace)));
    }

    [Theory]
    [InlineData("user", InstallScope.User)]
    [InlineData("global", InstallScope.User)]
    [InlineData("workspace", InstallScope.Workspace)]
    [InlineData("repo", InstallScope.Workspace)]
    [InlineData(null, InstallScope.Workspace)]
    public void ResolveScope_DefaultsToTheReviewableChoice(string? value, InstallScope expected)
    {
        string[] args = value is null ? ["hooks", "install"] : ["hooks", "install", "--scope", value];

        Assert.Equal(expected, HooksCommand.ResolveScope(args));
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
