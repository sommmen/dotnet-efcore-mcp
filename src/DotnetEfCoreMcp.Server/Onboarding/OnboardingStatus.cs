using DotnetEfCoreMcp.Server.Hooks;

namespace DotnetEfCoreMcp.Server.Onboarding;

/// <summary>
/// Whether one client's hooks are installed, at which scope.
/// </summary>
/// <param name="Client">The agent CLI.</param>
/// <param name="WorkspacePath">The repo-local config path.</param>
/// <param name="WorkspaceInstalled">Whether the repo-local config holds this tool's hooks.</param>
/// <param name="UserPath">The per-user config path.</param>
/// <param name="UserInstalled">Whether the per-user config holds this tool's hooks.</param>
public sealed record HookClientStatus(
    HookClient Client,
    string WorkspacePath,
    bool WorkspaceInstalled,
    string UserPath,
    bool UserInstalled)
{
    /// <summary>Whether this client has hooks at either scope.</summary>
    public bool Installed => WorkspaceInstalled || UserInstalled;
}

/// <summary>
/// Whether one skill is installed, at which scope.
/// </summary>
/// <param name="SkillName">The skill folder name.</param>
/// <param name="WorkspacePath">The repo-local destination.</param>
/// <param name="WorkspaceInstalled">Whether the repo-local destination holds the skill.</param>
/// <param name="UserPath">The per-user destination.</param>
/// <param name="UserInstalled">Whether the per-user destination holds the skill.</param>
public sealed record SkillStatus(
    string SkillName,
    string WorkspacePath,
    bool WorkspaceInstalled,
    string UserPath,
    bool UserInstalled)
{
    /// <summary>Whether this skill is installed at either scope.</summary>
    public bool Installed => WorkspaceInstalled || UserInstalled;
}

/// <summary>
/// A snapshot of everything optional this server can set up for an agent.
/// </summary>
/// <param name="Workspace">The repository the snapshot was taken for.</param>
/// <param name="Hooks">Per-client hook state.</param>
/// <param name="Skills">Per-skill state.</param>
/// <param name="SkillsAvailable">
/// Whether any skills are bundled with this build. <see langword="false"/> means skills cannot be
/// installed from here at all, which is a different situation from "not installed yet".
/// </param>
public sealed record OnboardingStatus(
    string Workspace,
    IReadOnlyList<HookClientStatus> Hooks,
    IReadOnlyList<SkillStatus> Skills,
    bool SkillsAvailable)
{
    /// <summary>The command to suggest when something is missing.</summary>
    public const string InstallCommandHint = "dnx DotnetEfCoreMcp.Server --yes --prerelease -- onboard install";

    /// <summary>Whether at least one client has hooks installed.</summary>
    public bool HooksInstalled => Hooks.Any(hook => hook.Installed);

    /// <summary>Whether the feedback skill is installed at either scope.</summary>
    public bool FeedbackSkillInstalled => Skills
        .Any(skill => skill.Installed
            && skill.SkillName.Equals(SkillCatalog.FeedbackSkillName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether everything available is installed somewhere.</summary>
    public bool FullyOnboarded => HooksInstalled && (!SkillsAvailable || Skills.All(skill => skill.Installed));
}

/// <summary>
/// Reports which onboarding artifacts are present for a workspace.
/// </summary>
/// <remarks>
/// Read-only by design, and used from three places that must agree: the <c>onboard status</c> CLI
/// verb, the <c>onboard</c> MCP tool, and the <c>SessionStart</c> hook that decides whether to
/// mention anything is missing.
/// </remarks>
public class OnboardingInspector
{
    private static readonly HookClient[] AllClients = [HookClient.Claude, HookClient.Copilot, HookClient.Codex];

    private readonly HookInstaller _hookInstaller;
    private readonly SkillInstaller _skillInstaller;

    /// <summary>Creates an inspector.</summary>
    /// <param name="hookInstaller">Hook installer used for its status checks.</param>
    /// <param name="skillInstaller">Skill installer used for its status checks.</param>
    public OnboardingInspector(HookInstaller? hookInstaller = null, SkillInstaller? skillInstaller = null)
    {
        _hookInstaller = hookInstaller ?? new HookInstaller();
        _skillInstaller = skillInstaller ?? new SkillInstaller();
    }

    /// <summary>Takes a snapshot for <paramref name="workspace"/>.</summary>
    public virtual OnboardingStatus Inspect(string workspace)
    {
        var root = Path.GetFullPath(workspace);

        var hooks = AllClients
            .Select(client => new HookClientStatus(
                client,
                HookInstaller.GetConfigPath(client, root, InstallScope.Workspace),
                IsHookInstalled(client, root, InstallScope.Workspace),
                HookInstaller.GetConfigPath(client, root, InstallScope.User),
                IsHookInstalled(client, root, InstallScope.User)))
            .ToList();

        var skills = _skillInstaller.AvailableSkills
            .Select(name => new SkillStatus(
                name,
                Path.Combine(SkillInstaller.GetSkillsDirectory(InstallScope.Workspace, root), name),
                _skillInstaller.IsInstalled(InstallScope.Workspace, root, name),
                Path.Combine(SkillInstaller.GetSkillsDirectory(InstallScope.User, root), name),
                _skillInstaller.IsInstalled(InstallScope.User, root, name)))
            .ToList();

        return new OnboardingStatus(root, hooks, skills, _skillInstaller.HasSkills);
    }

    private bool IsHookInstalled(HookClient client, string root, InstallScope scope) =>
        _hookInstaller.Status(client, root, scope).Message == "installed";
}
