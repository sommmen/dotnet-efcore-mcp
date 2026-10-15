using DotnetEfCoreMcp.Server.Hooks;
using DotnetEfCoreMcp.Server.Onboarding;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.Onboarding;

/// <summary>
/// Covers the <c>onboard</c> CLI verb: what it reports, what it writes, and the scope it writes to.
/// </summary>
[Collection(OnboardingEnvironmentCollection.Name)]
public class OnboardCommandTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "efcore-onboard-cli-tests", Guid.NewGuid().ToString("n"));

    private readonly string _repository;
    private readonly string _home;
    private readonly string? _previousHome;

    public OnboardCommandTests()
    {
        _repository = Path.Combine(_root, "repo");
        _home = Path.Combine(_root, "home");
        Directory.CreateDirectory(_repository);
        Directory.CreateDirectory(_home);

        _previousHome = Environment.GetEnvironmentVariable(UserDirectory.OverrideVariable);
        Environment.SetEnvironmentVariable(UserDirectory.OverrideVariable, _home);
    }

    [Fact]
    public void Run_DefaultsToStatusAndChangesNothing()
    {
        // Typing the bare verb to find out what it does must not install anything.
        var exitCode = Run(["onboard"], out var output);

        Assert.Equal(0, exitCode);
        Assert.Contains("not installed", output, StringComparison.Ordinal);
        Assert.False(File.Exists(HookInstaller.GetConfigPath(HookClient.Claude, _repository)));
    }

    [Fact]
    public void Run_StatusListsBothScopesForEveryClient()
    {
        Run(["onboard", "status", "--path", _repository], out var output);

        foreach (var client in (HookClient[])[HookClient.Claude, HookClient.Copilot, HookClient.Codex])
        {
            Assert.Contains(HookInstaller.ClientName(client), output, StringComparison.Ordinal);
        }

        Assert.Contains("workspace:", output, StringComparison.Ordinal);
        Assert.Contains("user:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_InstallWritesHooksAndSkillsIntoWorkspace()
    {
        var exitCode = Run(["onboard", "install", "--path", _repository], out _);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(HookInstaller.GetConfigPath(HookClient.Claude, _repository)));
        Assert.True(File.Exists(HookInstaller.GetConfigPath(HookClient.Codex, _repository)));
        Assert.True(Directory.Exists(Path.Combine(_repository, ".github", "skills")));
    }

    [Fact]
    public void Run_InstallHonorsUserScope()
    {
        Run(["onboard", "install", "--scope", "user", "--path", _repository, "--what", "hooks"], out _);

        Assert.True(File.Exists(Path.Combine(_home, ".claude", "settings.json")));
        Assert.True(File.Exists(Path.Combine(_home, ".codex", "hooks.json")));

        // A user-scoped install must leave the repository untouched.
        Assert.False(File.Exists(HookInstaller.GetConfigPath(HookClient.Claude, _repository)));
    }

    [Fact]
    public void Run_InstallCanSelectComponents()
    {
        Run(["onboard", "install", "--path", _repository, "--what", "hooks"], out _);

        Assert.True(File.Exists(HookInstaller.GetConfigPath(HookClient.Claude, _repository)));
        Assert.False(Directory.Exists(Path.Combine(_repository, ".github", "skills")));
    }

    [Fact]
    public void Run_InstallCanSelectOneClient()
    {
        Run(["onboard", "install", "--path", _repository, "--what", "hooks", "--client", "codex"], out _);

        Assert.True(File.Exists(HookInstaller.GetConfigPath(HookClient.Codex, _repository)));
        Assert.False(File.Exists(HookInstaller.GetConfigPath(HookClient.Claude, _repository)));
    }

    [Fact]
    public void Run_UninstallRemovesWhatInstallWrote()
    {
        Run(["onboard", "install", "--path", _repository], out _);
        Run(["onboard", "uninstall", "--path", _repository], out _);

        Assert.False(File.Exists(HookInstaller.GetConfigPath(HookClient.Claude, _repository)));
        Assert.False(Directory.Exists(Path.Combine(_repository, ".github", "skills", SkillCatalog.FeedbackSkillName)));
    }

    [Fact]
    public void Run_StatusReportsInstalledAfterInstall()
    {
        Run(["onboard", "install", "--path", _repository, "--what", "hooks"], out _);
        Run(["onboard", "status", "--path", _repository], out var output);

        Assert.Contains("installed", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_AcceptsOptionsInEqualsForm()
    {
        var exitCode = Run(["onboard", "install", $"--path={_repository}", "--what=hooks", "--client=codex"], out _);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(HookInstaller.GetConfigPath(HookClient.Codex, _repository)));
    }

    [Fact]
    public void Run_RejectsUnknownSubCommand()
    {
        var exitCode = Run(["onboard", "frobnicate"], out _, out var error);

        Assert.Equal(1, exitCode);
        Assert.Contains("Unknown", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_HelpDescribesBothScopes()
    {
        var exitCode = Run(["onboard", "--help"], out var output);

        Assert.Equal(0, exitCode);
        Assert.Contains("--scope", output, StringComparison.Ordinal);
        Assert.Contains("workspace", output, StringComparison.Ordinal);
        Assert.Contains("user", output, StringComparison.Ordinal);
    }

    private static int Run(string[] args, out string output) => Run(args, out output, out _);

    private static int Run(string[] args, out string output, out string error)
    {
        var outputWriter = new StringWriter();
        var errorWriter = new StringWriter();

        var exitCode = OnboardCommand.Run(args, outputWriter, errorWriter);

        output = outputWriter.ToString();
        error = errorWriter.ToString();
        return exitCode;
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
