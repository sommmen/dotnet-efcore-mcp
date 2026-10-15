using System.Text.Json;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.Hooks;
using DotnetEfCoreMcp.Server.Migrations;
using DotnetEfCoreMcp.Server.Mutations;
using DotnetEfCoreMcp.Server.Onboarding;
using DotnetEfCoreMcp.Server.Querying;
using DotnetEfCoreMcp.Server.Schema;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using DotnetEfCoreMcp.Server.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace DotnetEfCoreMcp.Server.Tests.Tools;

/// <summary>
/// Covers the <c>onboard</c> MCP tool, which lets the agent discover and (on request) install the
/// optional hooks and skills without the user having to find a CLI command first.
/// </summary>
[Collection(OnboardingEnvironmentCollection.Name)]
public sealed class EfCoreMcpToolsOnboardTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "efcore-onboard-tool-tests", Guid.NewGuid().ToString("n"));

    private readonly string _repository;
    private readonly string _home;
    private readonly string? _previousHome;

    public EfCoreMcpToolsOnboardTests()
    {
        _repository = Path.Combine(_root, "repo");
        _home = Path.Combine(_root, "home");
        Directory.CreateDirectory(_repository);
        Directory.CreateDirectory(_home);

        _previousHome = Environment.GetEnvironmentVariable(UserDirectory.OverrideVariable);
        Environment.SetEnvironmentVariable(UserDirectory.OverrideVariable, _home);
    }

    [Fact]
    public void Onboard_DefaultsToReadOnlyStatus()
    {
        // The agent is expected to call this unprompted, so the default must not write anything.
        using var document = JsonDocument.Parse(CreateTools().Onboard(_repository));
        var root = document.RootElement;

        Assert.Equal(Path.GetFullPath(_repository), root.GetProperty("workspace").GetString());
        Assert.False(root.GetProperty("fullyOnboarded").GetBoolean());

        // Nothing was applied, so the formatter omits the property entirely.
        Assert.False(root.TryGetProperty("applied", out _));
        Assert.False(File.Exists(HookInstaller.GetConfigPath(HookClient.Claude, _repository)));
    }

    [Fact]
    public void Onboard_StatusReportsEveryClientAndBothScopes()
    {
        using var document = JsonDocument.Parse(CreateTools().Onboard(_repository));

        var hooks = document.RootElement.GetProperty("hooks").EnumerateArray().ToList();
        Assert.Equal(3, hooks.Count);

        foreach (var hook in hooks)
        {
            Assert.False(hook.GetProperty("workspaceInstalled").GetBoolean());
            Assert.False(hook.GetProperty("userInstalled").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(hook.GetProperty("workspacePath").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(hook.GetProperty("userPath").GetString()));
        }
    }

    [Fact]
    public void Onboard_StatusTellsTheAgentToAskFirst()
    {
        using var document = JsonDocument.Parse(CreateTools().Onboard(_repository));

        Assert.Contains("Ask the user", document.RootElement.GetProperty("nextStep").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Onboard_ApplyIsRefusedUnlessExplicitlyAllowed()
    {
        // An installed hook adds a command the agent CLI runs on every later tool call, so writing
        // it must be an operator decision rather than something a tool description asks for nicely.
        var exception = Assert.Throws<McpException>(() =>
            CreateTools(allowInstall: false).Onboard(_repository, apply: true));

        Assert.Contains("Onboarding:AllowInstall", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(HookInstaller.GetConfigPath(HookClient.Claude, _repository)));
        Assert.False(Directory.Exists(Path.Combine(_repository, ".github", "skills")));
    }

    [Fact]
    public void Onboard_InstallIsDisabledWhenNoOptionIsConfigured()
    {
        // Covers the default rather than an explicitly-false option: a safety gate that only works
        // when someone remembers to pass it is not a safety gate.
        Assert.False(new OnboardingOptions().AllowInstall);

        var exception = Assert.Throws<McpException>(() =>
            CreateToolsWithoutOnboardingOptions().Onboard(_repository, apply: true));

        Assert.Contains("Onboarding:AllowInstall", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Onboard_StatusStillWorksWhenInstallIsDisabled()
    {
        // The read-only path is the whole discoverability story, so the gate must not block it.
        using var document = JsonDocument.Parse(CreateTools(allowInstall: false).Onboard(_repository));

        Assert.Equal(3, document.RootElement.GetProperty("hooks").EnumerateArray().Count());
    }

    [Fact]
    public void Onboard_RefusalPointsAtTheCliCommand()
    {
        // Refusing has to stay actionable: the user can still run the install themselves.
        var exception = Assert.Throws<McpException>(() =>
            CreateTools(allowInstall: false).Onboard(_repository, apply: true));

        Assert.Contains(OnboardingStatus.InstallCommandHint, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Onboard_ApplyInstallsHooksIntoTheWorkspace()
    {
        using var document = JsonDocument.Parse(
            CreateTools().Onboard(_repository, apply: true, what: "hooks"));
        var root = document.RootElement;

        Assert.True(File.Exists(HookInstaller.GetConfigPath(HookClient.Claude, _repository)));
        Assert.NotEmpty(root.GetProperty("applied").EnumerateArray());
        Assert.True(root.GetProperty("hooks").EnumerateArray()
            .All(hook => hook.GetProperty("workspaceInstalled").GetBoolean()));
    }

    [Fact]
    public void Onboard_ApplyHonorsUserScope()
    {
        CreateTools().Onboard(_repository, apply: true, scope: "user", what: "hooks");

        Assert.True(File.Exists(Path.Combine(_home, ".claude", "settings.json")));
        Assert.False(File.Exists(HookInstaller.GetConfigPath(HookClient.Claude, _repository)));
    }

    [Fact]
    public void Onboard_ApplyCanInstallSkillsOnly()
    {
        CreateTools().Onboard(_repository, apply: true, what: "skills");

        Assert.True(Directory.Exists(Path.Combine(_repository, ".github", "skills")));
        Assert.False(File.Exists(HookInstaller.GetConfigPath(HookClient.Claude, _repository)));
    }

    [Fact]
    public void Onboard_ReportsStatusAfterApplyingRatherThanBefore()
    {
        // Returning the pre-install state would tell the agent its own install had not happened.
        using var document = JsonDocument.Parse(
            CreateTools().Onboard(_repository, apply: true, what: "hooks"));

        Assert.Contains("Restart", document.RootElement.GetProperty("nextStep").GetString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sideways")]
    [InlineData("machine")]
    public void Onboard_RejectsUnknownScope(string scope) =>
        Assert.Contains("Unsupported scope",
            Assert.Throws<McpException>(() => CreateTools().Onboard(_repository, scope: scope)).Message,
            StringComparison.Ordinal);

    [Fact]
    public void Onboard_RejectsUnknownComponent() =>
        Assert.Contains("Unsupported value",
            Assert.Throws<McpException>(() => CreateTools().Onboard(_repository, what: "everything")).Message,
            StringComparison.Ordinal);

    [Fact]
    public void Onboard_RejectsAMissingWorkspace() =>
        Assert.Contains("does not exist",
            Assert.Throws<McpException>(() => CreateTools().Onboard(Path.Combine(_root, "missing"))).Message,
            StringComparison.Ordinal);

    [Theory]
    [InlineData("workspace")]
    [InlineData("repo")]
    [InlineData("user")]
    [InlineData("global")]
    public void Onboard_AcceptsScopeAliases(string scope)
    {
        using var document = JsonDocument.Parse(CreateTools().Onboard(_repository, scope: scope));

        Assert.Contains(document.RootElement.GetProperty("scope").GetString(), (string[])["workspace", "user"]);
    }

    /// <summary>Builds the tools with no onboarding options supplied at all, exercising the default.</summary>
    private static EfCoreMcpTools CreateToolsWithoutOnboardingOptions()
    {
        var configuration = new ConfigurationBuilder().Build();
        var rawSqlOptions = new RawSqlExecutionOptions();

        return new EfCoreMcpTools(
            new AssemblyLoaderService(),
            new AssemblyDiscoveryService(),
            new ConnectionRegistry(configuration),
            new SchemaCache(),
            new RoslynQueryExecutor(new QueryExecutionOptions(), new QueryCompiler(new QueryCompilationOptions())),
            new OutOfProcessRoslynQueryExecutor(new QueryExecutionOptions()),
            new QueryExecutionOptions(),
            rawSqlOptions,
            new SqlQueryExecutor(rawSqlOptions, NullLogger<SqlQueryExecutor>.Instance),
            new MigrationsOptions(),
            new MigrationInspector(new MigrationsOptions(), NullLogger<MigrationInspector>.Instance),
            new JsonToolResultFormatter(),
            new ToolDiagnosticsOptions(),
            NullLogger<EfCoreMcpTools>.Instance,
            new EntityMutationsOptions(),
            new EntityMutationExecutor(NullLogger<EntityMutationExecutor>.Instance));
    }

    private static EfCoreMcpTools CreateTools(bool allowInstall = true)
    {
        var configuration = new ConfigurationBuilder().Build();
        var rawSqlOptions = new RawSqlExecutionOptions();

        return new EfCoreMcpTools(
            new AssemblyLoaderService(),
            new AssemblyDiscoveryService(),
            new ConnectionRegistry(configuration),
            new SchemaCache(),
            new RoslynQueryExecutor(new QueryExecutionOptions(), new QueryCompiler(new QueryCompilationOptions())),
            new OutOfProcessRoslynQueryExecutor(new QueryExecutionOptions()),
            new QueryExecutionOptions(),
            rawSqlOptions,
            new SqlQueryExecutor(rawSqlOptions, NullLogger<SqlQueryExecutor>.Instance),
            new MigrationsOptions(),
            new MigrationInspector(new MigrationsOptions(), NullLogger<MigrationInspector>.Instance),
            new JsonToolResultFormatter(),
            new ToolDiagnosticsOptions(),
            NullLogger<EfCoreMcpTools>.Instance,
            new EntityMutationsOptions(),
            new EntityMutationExecutor(NullLogger<EntityMutationExecutor>.Instance),
            new OnboardingOptions { AllowInstall = allowInstall });
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
