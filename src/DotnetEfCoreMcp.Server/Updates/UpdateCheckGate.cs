namespace DotnetEfCoreMcp.Server.Updates;

/// <summary>
/// Decides whether an update check may run at all.
/// </summary>
/// <remarks>
/// <para>
/// The check is on by default: it is an unauthenticated GET for a public package's version list on
/// the same feed this package is published to, carrying no user data and no payload - the same
/// request a human makes by opening the package page.
/// </para>
/// <para>
/// It is nevertheless gated several ways, because the ecosystem's clearest lesson about update
/// checks is that unsolicited network work as a side effect of an unrelated command is what users
/// object to. Every gate here fails closed: anything unexpected means no check.
/// </para>
/// <para>
/// Notably absent is a TTY gate, which GitHub CLI and update-notifier both use. An MCP stdio server
/// never has a terminal, so requiring one would disable the feature outright.
/// </para>
/// </remarks>
public sealed class UpdateCheckGate
{
    /// <summary>Set to any value to disable update checks entirely.</summary>
    /// <remarks>
    /// Follows the <c>GH_NO_UPDATE_NOTIFIER</c> / <c>CHECKPOINT_DISABLE</c> naming convention:
    /// product prefix, then the thing being turned off.
    /// </remarks>
    public const string DisableVariable = "DOTNETEFCOREMCP_NO_UPDATE_CHECK";

    /// <summary>The .NET CLI's telemetry opt-out, honored here as an update-check opt-out too.</summary>
    /// <remarks>
    /// Someone who opted out of .NET phoning home did not mean "except for this one". Honoring it
    /// costs nothing and respects the evident intent.
    /// </remarks>
    public const string DotnetTelemetryOptOutVariable = "DOTNET_CLI_TELEMETRY_OPTOUT";

    /// <summary>
    /// Environment variables that indicate a CI run.
    /// </summary>
    /// <remarks>
    /// A subset of what <c>ci-info</c> checks, covering the providers this project plausibly runs
    /// under. Exhaustiveness is not important: a missed provider means one harmless HTTP request.
    /// </remarks>
    private static readonly string[] CiVariables =
    [
        "CI",
        "CONTINUOUS_INTEGRATION",
        "BUILD_ID",
        "BUILD_NUMBER",
        "GITHUB_ACTIONS",
        "TF_BUILD",
        "TEAMCITY_VERSION",
        "JENKINS_URL",
        "GITLAB_CI",
        "CIRCLECI",
        "TRAVIS",
        "APPVEYOR",
        "CODESPACES",
    ];

    private readonly Func<string, string?> _environment;
    private readonly bool _enabledByConfiguration;

    /// <summary>
    /// Creates a gate reading the process environment.
    /// </summary>
    /// <param name="enabledByConfiguration">
    /// The <c>UpdateCheck:Enabled</c> configuration value. Defaults to enabled.
    /// </param>
    public UpdateCheckGate(bool enabledByConfiguration = true)
        : this(enabledByConfiguration, Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>
    /// Creates a gate over an explicit environment, so tests need not mutate the real one.
    /// </summary>
    internal UpdateCheckGate(bool enabledByConfiguration, Func<string, string?> environment)
    {
        _enabledByConfiguration = enabledByConfiguration;
        _environment = environment;
    }

    /// <summary>
    /// Reports whether a check may run for the given version and install method.
    /// </summary>
    /// <param name="installedVersion">The running version, or null when unknown.</param>
    /// <param name="installMethod">How this server was installed.</param>
    public bool IsAllowed(string? installedVersion, InstallMethod installMethod)
    {
        if (!_enabledByConfiguration)
        {
            return false;
        }

        if (IsSet(DisableVariable))
        {
            return false;
        }

        if (_environment(DotnetTelemetryOptOutVariable) is "1" or "true" or "TRUE")
        {
            return false;
        }

        if (IsContinuousIntegration())
        {
            return false;
        }

        // Nothing to compare against: a build without packaging metadata, such as a local F5.
        if (string.IsNullOrWhiteSpace(installedVersion))
        {
            return false;
        }

        // An unparseable version cannot be compared, and guessing is worse than silence.
        if (!NuGet.Versioning.NuGetVersion.TryParse(installedVersion, out _))
        {
            return false;
        }

        // Only install methods with a real upgrade command get checked. Everything else either
        // updates itself or cannot be named, and in both cases a network call would buy nothing.
        return InstallMethodDetector.ShouldNotify(installMethod);
    }

    /// <summary>
    /// Reports whether this process looks like it is running in CI.
    /// </summary>
    /// <remarks>
    /// <c>CI=false</c> is honored as an explicit escape hatch, matching <c>ci-info</c>: a developer
    /// working inside an environment that merely looks like CI can force the normal behavior.
    /// </remarks>
    private bool IsContinuousIntegration()
    {
        if (string.Equals(_environment("CI"), "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var variable in CiVariables)
        {
            if (IsSet(variable))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsSet(string variable) => !string.IsNullOrWhiteSpace(_environment(variable));
}
