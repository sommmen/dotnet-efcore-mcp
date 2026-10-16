using DotnetEfCoreMcp.Server.Updates;

namespace DotnetEfCoreMcp.Server.Tests.Updates;

/// <summary>
/// Covers the conditions under which an update check is allowed to make a network request.
/// </summary>
/// <remarks>
/// Every gate must fail closed. These tests exist because the cost of a gate silently not working
/// is an unsolicited outbound request in an environment that did not want one - which is the single
/// thing users object to most about update checks.
/// </remarks>
public class UpdateCheckGateTests
{
    private const string Version = "0.1.32-preview";

    private static UpdateCheckGate Create(
        IDictionary<string, string?>? environment = null,
        bool enabledByConfiguration = true)
    {
        var variables = environment ?? new Dictionary<string, string?>();

        return new UpdateCheckGate(
            enabledByConfiguration,
            name => variables.TryGetValue(name, out var value) ? value : null);
    }

    [Fact]
    public void Allows_a_check_by_default_for_a_global_install()
    {
        Assert.True(Create().IsAllowed(Version, InstallMethod.GlobalTool));
    }

    [Fact]
    public void Blocks_when_the_opt_out_variable_is_set_to_anything()
    {
        // Presence is what matters, not the value, matching GH_NO_UPDATE_NOTIFIER. Someone who
        // sets it to "0" meant to turn the feature off, not on.
        foreach (var value in new[] { "1", "0", "true", "anything" })
        {
            var gate = Create(new Dictionary<string, string?>
            {
                [UpdateCheckGate.DisableVariable] = value,
            });

            Assert.False(gate.IsAllowed(Version, InstallMethod.GlobalTool));
        }
    }

    [Fact]
    public void Blocks_when_configuration_disables_it()
    {
        Assert.False(Create(enabledByConfiguration: false).IsAllowed(Version, InstallMethod.GlobalTool));
    }

    [Fact]
    public void Blocks_when_dotnet_telemetry_is_opted_out()
    {
        var gate = Create(new Dictionary<string, string?>
        {
            [UpdateCheckGate.DotnetTelemetryOptOutVariable] = "1",
        });

        // Someone who opted out of .NET phoning home did not mean "except this".
        Assert.False(gate.IsAllowed(Version, InstallMethod.GlobalTool));
    }

    [Theory]
    [InlineData("CI")]
    [InlineData("GITHUB_ACTIONS")]
    [InlineData("TF_BUILD")]
    [InlineData("JENKINS_URL")]
    [InlineData("CODESPACES")]
    public void Blocks_in_continuous_integration(string variable)
    {
        var gate = Create(new Dictionary<string, string?> { [variable] = "true" });

        Assert.False(gate.IsAllowed(Version, InstallMethod.GlobalTool));
    }

    [Fact]
    public void Honors_CI_false_as_an_escape_hatch()
    {
        // Matches ci-info: a developer inside an environment that merely looks like CI can force
        // the normal behavior back on.
        var gate = Create(new Dictionary<string, string?> { ["CI"] = "false" });

        Assert.True(gate.IsAllowed(Version, InstallMethod.GlobalTool));
    }

    [Fact]
    public void Blocks_when_no_version_is_known()
    {
        // A local F5 build has no packaging metadata, so there is nothing to compare against.
        Assert.False(Create().IsAllowed(null, InstallMethod.GlobalTool));
        Assert.False(Create().IsAllowed(string.Empty, InstallMethod.GlobalTool));
    }

    [Fact]
    public void Blocks_when_the_version_cannot_be_parsed()
    {
        Assert.False(Create().IsAllowed("not-a-version", InstallMethod.GlobalTool));
    }

    [Theory]
    [InlineData(InstallMethod.Unknown)]
    [InlineData(InstallMethod.Dnx)]
    [InlineData(InstallMethod.Npm)]
    public void Blocks_for_install_methods_that_would_get_no_notice_anyway(InstallMethod method)
    {
        // No point spending a request to compute something that will never be shown.
        Assert.False(Create().IsAllowed(Version, method));
    }

    [Fact]
    public void Allows_a_check_for_a_local_tool_install()
    {
        Assert.True(Create().IsAllowed(Version, InstallMethod.LocalTool));
    }
}
