namespace DotnetEfCoreMcp.Server.Updates;

/// <summary>
/// The <c>version</c> CLI verb: reports the running version and how it was installed.
/// </summary>
/// <remarks>
/// <para>
/// This is the one update surface that can print freely to stdout, because CLI verbs are dispatched
/// before the stdio MCP server starts and the process exits without ever opening the JSON-RPC
/// channel. Every in-server surface is constrained by the stdio transport's rule that stdout
/// carries nothing but MCP messages.
/// </para>
/// <para>
/// Modeled on <c>dotnet sdk check</c>: on demand, never nagging.
/// </para>
/// </remarks>
public static class VersionCommand
{
    /// <summary>The verb that routes to this command.</summary>
    public const string VerbName = "version";

    /// <summary>Flag that makes the command query the feed for a newer version.</summary>
    /// <remarks>
    /// Opt-in rather than automatic so that <c>version</c> stays instant and offline-safe by
    /// default; someone who just wants to know what they are running should not wait on a network
    /// call to find out.
    /// </remarks>
    public const string CheckUpdatesFlag = "--check-updates";

    /// <summary>
    /// Runs the command.
    /// </summary>
    /// <param name="args">Arguments including the leading <c>version</c> verb.</param>
    /// <param name="output">Destination for normal output.</param>
    /// <param name="error">Destination for diagnostics.</param>
    /// <returns>The process exit code.</returns>
    public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error) =>
        RunAsync(args, output, error, checker: null);

    /// <summary>
    /// Runs the command against an explicit checker, so tests need no network.
    /// </summary>
    /// <param name="args">Arguments including the leading <c>version</c> verb.</param>
    /// <param name="output">Destination for normal output.</param>
    /// <param name="error">Destination for diagnostics.</param>
    /// <param name="checker">Checker to use, or null to build the default one on demand.</param>
    internal static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        UpdateChecker? checker)
    {
        if (args.Length >= 2 && args[1] is "--help" or "-h" or "help")
        {
            WriteUsage(output);
            return 0;
        }

        var version = PackageVersionProvider.GetPackageVersion();
        var method = new InstallMethodDetector().Detect();

        output.WriteLine($"dotnet-efcore-mcp {version ?? "(unknown - built without packaging metadata)"}");
        output.WriteLine($"Installed via:   {Describe(method)}");

        if (version is null)
        {
            // Without a version there is nothing to compare, so say so rather than printing an
            // upgrade command that cannot be checked against anything.
            output.WriteLine();
            output.WriteLine("This build carries no package version, so update checks are skipped.");
            return 0;
        }

        var includePrerelease = VersionChannel.IsPrerelease(version);
        var upgrade = InstallMethodDetector.GetUpgradeCommand(method, includePrerelease);

        if (upgrade is not null)
        {
            output.WriteLine($"Upgrade with:    {upgrade}");
        }
        else
        {
            output.WriteLine($"Upgrade:         {DescribeNoUpgrade(method)}");
        }

        if (!HasFlag(args, CheckUpdatesFlag))
        {
            return 0;
        }

        output.WriteLine();

        var result = await (checker ?? CreateChecker()).CheckAsync().ConfigureAwait(false);

        if (result.UpdateAvailable)
        {
            output.WriteLine($"Latest version:  {result.LatestVersion} (update available)");
        }
        else if (result.Checked)
        {
            output.WriteLine("Latest version:  up to date");
        }
        else
        {
            // Covers both "turned off" and "could not reach the feed". Distinguishing them would
            // mean reporting on the user's network from a command about versions.
            output.WriteLine("Latest version:  not checked (disabled, or the feed was unreachable)");
        }

        return 0;
    }

    /// <summary>
    /// Builds the production checker.
    /// </summary>
    /// <remarks>
    /// Constructed here rather than taken from DI because CLI verbs run before the host exists.
    /// </remarks>
    private static UpdateChecker CreateChecker()
    {
        var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        return new UpdateChecker(
            new FlatContainerVersionLookup(httpClient),
            new UpdateCheckCache(),
            new UpdateCheckGate(),
            new InstallMethodDetector());
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(argument => argument.Equals(flag, StringComparison.OrdinalIgnoreCase));

    private static string Describe(InstallMethod method) => method switch
    {
        InstallMethod.GlobalTool => "dotnet tool install --global",
        InstallMethod.LocalTool => "dotnet tool install --local (manifest)",
        InstallMethod.Dnx => "dnx / dotnet tool exec",
        InstallMethod.Npm => "npm wrapper (npx dotnet-efcore-mcp)",
        _ => "unknown",
    };

    private static string DescribeNoUpgrade(InstallMethod method) => method switch
    {
        InstallMethod.Dnx => "not needed - dnx fetches the newest version on each run",
        InstallMethod.Npm => "not needed - the npm wrapper updates the tool before starting it",
        _ => "install method could not be determined, so no upgrade command is suggested",
    };

    private static void WriteUsage(TextWriter output)
    {
        output.WriteLine("Usage: dotnet-efcore-mcp version [--check-updates]");
        output.WriteLine();
        output.WriteLine("Reports the running version, how this server was installed, and how to upgrade it.");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine("  --check-updates   Also query nuget.org for a newer version.");
    }
}
