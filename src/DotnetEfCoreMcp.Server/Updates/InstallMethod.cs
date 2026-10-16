namespace DotnetEfCoreMcp.Server.Updates;

/// <summary>
/// How this server process was installed, as far as it can be determined from inside the process.
/// </summary>
/// <remarks>
/// There is no runtime API that reports this, so every value except <see cref="Npm"/> is inferred
/// from filesystem layout and is therefore a best guess. <see cref="Unknown"/> is a first-class
/// outcome, not a failure: it is what the detector returns whenever the evidence is ambiguous, and
/// it suppresses update notices rather than risking a wrong upgrade command.
/// </remarks>
public enum InstallMethod
{
    /// <summary>Could not be determined. No update notice is shown.</summary>
    Unknown = 0,

    /// <summary>Installed by <c>dotnet tool install --global</c>. The only notified method.</summary>
    GlobalTool,

    /// <summary>Installed into a tool manifest by <c>dotnet tool install --local</c>.</summary>
    LocalTool,

    /// <summary>Run via <c>dnx</c>/<c>dotnet tool exec</c>, which refetches per invocation.</summary>
    Dnx,

    /// <summary>Launched by the npm wrapper, which updates the .NET tool before starting it.</summary>
    Npm,
}
