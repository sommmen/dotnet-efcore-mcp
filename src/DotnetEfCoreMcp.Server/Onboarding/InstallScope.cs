namespace DotnetEfCoreMcp.Server.Onboarding;

/// <summary>
/// Where an onboarding artifact (hook config or skill) is written.
/// </summary>
/// <remarks>
/// Both scopes are first-class rather than one being a fallback for the other: a team that wants
/// everyone to get the same reminders commits the workspace copy, while an individual who uses this
/// server across many repositories installs once at user level and never thinks about it again.
/// </remarks>
public enum InstallScope
{
    /// <summary>
    /// Repo-local files under the target repository, intended to be committed so the whole team
    /// gets them on clone.
    /// </summary>
    Workspace,

    /// <summary>
    /// Per-user files under the home directory, applying to every repository the user opens.
    /// </summary>
    User,
}
