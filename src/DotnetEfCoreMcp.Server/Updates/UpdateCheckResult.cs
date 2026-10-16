namespace DotnetEfCoreMcp.Server.Updates;

/// <summary>
/// The outcome of an update check.
/// </summary>
/// <param name="InstalledVersion">The running version, or null when it could not be determined.</param>
/// <param name="LatestVersion">The newest version available on this build's channel, if any.</param>
/// <param name="InstallMethod">How this server was installed.</param>
/// <param name="UpgradeCommand">Command that performs the upgrade, when one applies.</param>
/// <param name="Checked">
/// Whether a check actually ran. False means it was gated off, skipped, or has not finished yet -
/// which is different from "ran and found nothing", and is reported differently.
/// </param>
public sealed record UpdateCheckResult(
    string? InstalledVersion,
    string? LatestVersion,
    InstallMethod InstallMethod,
    string? UpgradeCommand,
    bool Checked)
{
    /// <summary>
    /// Whether a newer version is available and there is a command to get it.
    /// </summary>
    public bool UpdateAvailable => LatestVersion is not null && UpgradeCommand is not null;

    /// <summary>
    /// A one-line, human-readable notice, or <see langword="null"/> when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// Phrased as a statement rather than an instruction to the agent. An agent reading this should
    /// pass it along if it is relevant, not treat it as a task to go and perform - the user decides
    /// whether to upgrade.
    /// </remarks>
    public string? Notice => UpdateAvailable
        ? $"A newer version of dotnet-efcore-mcp is available: {InstalledVersion} -> {LatestVersion}. "
          + $"To upgrade, run: {UpgradeCommand}"
        : null;

    /// <summary>Creates a result for a check that did not run.</summary>
    public static UpdateCheckResult NotChecked(string? installedVersion, InstallMethod installMethod) =>
        new(installedVersion, LatestVersion: null, installMethod, UpgradeCommand: null, Checked: false);
}
