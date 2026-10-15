using System.Text.Json;
using DotnetEfCoreMcp.Server.Hooks;
using DotnetEfCoreMcp.Server.Onboarding;
using DotnetEfCoreMcp.Server.Tests.TestSupport;

namespace DotnetEfCoreMcp.Server.Tests.Hooks;

/// <summary>
/// Covers the <c>SessionStart</c> activation context: when it speaks, when it stays quiet, and its
/// fail-open contract.
/// </summary>
[Collection(OnboardingEnvironmentCollection.Name)]
public class SessionActivationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "efcore-activation-tests", Guid.NewGuid().ToString("n"));

    private readonly string _efCoreWorkspace;
    private readonly string _plainWorkspace;
    private readonly string? _previousHome;

    public SessionActivationTests()
    {
        _efCoreWorkspace = Path.Combine(_root, "efcore-app");
        _plainWorkspace = Path.Combine(_root, "frontend");
        Directory.CreateDirectory(_efCoreWorkspace);
        Directory.CreateDirectory(_plainWorkspace);

        _previousHome = Environment.GetEnvironmentVariable(UserDirectory.OverrideVariable);
        Environment.SetEnvironmentVariable(UserDirectory.OverrideVariable, Path.Combine(_root, "home"));

        File.WriteAllText(
            Path.Combine(_efCoreWorkspace, "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="9.0.0" />
              </ItemGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(_plainWorkspace, "package.json"), """{ "name": "frontend" }""");
    }

    [Fact]
    public void Evaluate_WorkspaceScopeAlwaysAnnounces()
    {
        // A repo-local hook was installed into this specific repository, which is the opt-in.
        var response = Evaluate(_plainWorkspace, InstallScope.Workspace);

        Assert.Contains("dotnet-efcore-mcp is available", ReadContext(response), StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_UserScopeStaysQuietOutsideEfCoreWorkspaces()
    {
        // A user-level hook fires in every session on the machine, so EF Core guidance in a
        // front-end repo would be pure noise.
        Assert.Equal("{}", Evaluate(_plainWorkspace, InstallScope.User));
    }

    [Fact]
    public void Evaluate_UserScopeAnnouncesInEfCoreWorkspaces()
    {
        Assert.Contains("dotnet-efcore-mcp is available",
            ReadContext(Evaluate(_efCoreWorkspace, InstallScope.User)), StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_NamesTheSchemaAndValidationTools()
    {
        var context = ReadContext(Evaluate(_efCoreWorkspace, InstallScope.User));

        Assert.Contains("get_entity_schema", context, StringComparison.Ordinal);
        Assert.Contains("preview_query_sql", context, StringComparison.Ordinal);
        Assert.Contains("run_query", context, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_MentionsMissingOnboardingOnce()
    {
        var context = ReadContext(Evaluate(_efCoreWorkspace, InstallScope.Workspace));

        Assert.Contains("onboard", context, StringComparison.Ordinal);

        // Discoverability must not become permission: the agent is told to ask first.
        Assert.Contains("without asking", context, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_OmitsSetupNoteWhenNothingIsMissing()
    {
        // A fully set-up session should pay for the tool guidance and nothing more.
        var status = new OnboardingStatus(
            _efCoreWorkspace,
            [new HookClientStatus(HookClient.Claude, "w", true, "u", false)],
            [new SkillStatus(SkillCatalog.FeedbackSkillName, "w", true, "u", false)],
            SkillsAvailable: true);

        var context = ReadContext(
            new SessionActivation(new StubInspector(status))
                .Evaluate(Payload(_efCoreWorkspace), HookClient.Claude, InstallScope.Workspace));

        Assert.DoesNotContain("not yet installed", context, StringComparison.Ordinal);
        Assert.Contains("dotnet-efcore-mcp is available", context, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_EmitsBothOutputEnvelopes()
    {
        // Copilot reads a top-level additionalContext; Claude and Codex read the nested one.
        using var document = JsonDocument.Parse(Evaluate(_efCoreWorkspace, InstallScope.Workspace));

        Assert.True(document.RootElement.TryGetProperty("additionalContext", out _));
        Assert.Equal("SessionStart",
            document.RootElement.GetProperty("hookSpecificOutput").GetProperty("hookEventName").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    public void Evaluate_NeverThrowsOnBadPayloads(string? payload)
    {
        // Session start must never be the thing that breaks a session.
        var response = new SessionActivation().Evaluate(payload, HookClient.Claude, InstallScope.User);

        Assert.False(string.IsNullOrWhiteSpace(response));
        using var document = JsonDocument.Parse(response);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    [Fact]
    public void Evaluate_ReadsWorkspaceFromEitherFieldSpelling()
    {
        foreach (var field in (string[])["cwd", "workspaceRoot", "workspace_root", "projectDir", "project_dir"])
        {
            var payload = $$"""{"session_id":"s","{{field}}":{{JsonSerializer.Serialize(_efCoreWorkspace)}}}""";

            Assert.NotEqual("{}",
                new SessionActivation().Evaluate(payload, HookClient.Claude, InstallScope.User));
        }
    }

    private static string Payload(string workspace) =>
        $$"""{"session_id":"test-session","cwd":{{JsonSerializer.Serialize(workspace)}}}""";

    private static string Evaluate(string workspace, InstallScope scope) =>
        new SessionActivation().Evaluate(Payload(workspace), HookClient.Claude, scope);

    private static string ReadContext(string response)
    {
        using var document = JsonDocument.Parse(response);
        return document.RootElement.GetProperty("additionalContext").GetString() ?? string.Empty;
    }

    /// <summary>Returns a fixed status so the "nothing missing" branch can be exercised.</summary>
    private sealed class StubInspector(OnboardingStatus status) : OnboardingInspector
    {
        public override OnboardingStatus Inspect(string workspace) => status;
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
