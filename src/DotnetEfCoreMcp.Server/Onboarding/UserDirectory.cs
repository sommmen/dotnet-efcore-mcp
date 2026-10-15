namespace DotnetEfCoreMcp.Server.Onboarding;

/// <summary>
/// Resolves the home directory that user-scoped installs are written under.
/// </summary>
/// <remarks>
/// <see cref="Environment.SpecialFolder.UserProfile"/> is used rather than <c>HOME</c>/<c>USERPROFILE</c>
/// directly because it is the one spelling that is correct on every platform. The override exists so
/// tests can install into a temporary directory instead of the real profile, which a test must never
/// touch.
/// </remarks>
public static class UserDirectory
{
    /// <summary>Environment variable that redirects user-scoped installs, for tests and sandboxes.</summary>
    public const string OverrideVariable = "DOTNETEFCOREMCP_HOMEDIR";

    /// <summary>Returns the directory user-scoped artifacts are written under.</summary>
    public static string Resolve()
    {
        var configured = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // SpecialFolder.UserProfile can come back empty in stripped-down container images; falling
        // back to the current directory keeps a user-scoped install diagnosable (it writes
        // somewhere visible and reports the path) instead of throwing from a path-combine.
        return string.IsNullOrWhiteSpace(profile) ? Directory.GetCurrentDirectory() : profile;
    }
}
