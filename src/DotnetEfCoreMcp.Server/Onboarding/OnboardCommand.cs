using DotnetEfCoreMcp.Server.Hooks;

namespace DotnetEfCoreMcp.Server.Onboarding;

/// <summary>
/// The <c>onboard</c> CLI verb: one entry point for every optional thing this server can set up.
/// </summary>
/// <remarks>
/// The individual pieces were already installable - <c>hooks install</c> for the reminders, a
/// PowerShell/bash script for the skills - but a user had to know both existed, and they were
/// documented in separate README sections. This verb exists so that discovering one feature means
/// discovering all of them: <c>onboard status</c> lists everything available, installed or not, and
/// <c>onboard install</c> sets up whatever the user picks.
/// </remarks>
public static class OnboardCommand
{
    /// <summary>The verb that routes to this command.</summary>
    public const string VerbName = "onboard";

    /// <summary>What an install/uninstall should act on.</summary>
    private enum Component
    {
        Hooks,
        Skills,
    }

    /// <summary>
    /// Runs the command.
    /// </summary>
    /// <param name="args">Arguments including the leading <c>onboard</c> verb.</param>
    /// <param name="output">Destination for normal output.</param>
    /// <param name="error">Destination for diagnostics.</param>
    /// <returns>The process exit code.</returns>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        // Bare `onboard` shows status rather than erroring: someone who types the verb to find out
        // what it does should get the answer, not a usage block, and status changes nothing.
        var subCommand = args.Length >= 2 ? args[1].ToLowerInvariant() : "status";

        return subCommand switch
        {
            "status" => RunStatus(args, output),
            "install" => RunInstall(args, output),
            "uninstall" => RunUninstall(args, output),
            "--help" or "-h" or "help" => WriteUsageAndSucceed(output),
            _ => WriteUnknown(subCommand, error),
        };
    }

    private static int RunStatus(string[] args, TextWriter output)
    {
        var root = ResolveRepositoryRoot(args);
        var status = CreateInspector(args).Inspect(root);

        output.WriteLine($"dotnet-efcore-mcp onboarding for {status.Workspace}");
        output.WriteLine();
        output.WriteLine("Hooks (announce the server at session start; remind the agent to validate LINQ):");

        foreach (var hook in status.Hooks)
        {
            output.WriteLine($"  {HookInstaller.ClientName(hook.Client),-8} workspace: {Describe(hook.WorkspaceInstalled)}  {hook.WorkspacePath}");
            output.WriteLine($"  {string.Empty,-8} user:      {Describe(hook.UserInstalled)}  {hook.UserPath}");
        }

        output.WriteLine();
        output.WriteLine("Skills (workflows the agent reads when a description matches the task):");

        if (!status.SkillsAvailable)
        {
            output.WriteLine("  (no skills are bundled with this build)");
        }
        else
        {
            foreach (var skill in status.Skills)
            {
                output.WriteLine($"  {skill.SkillName}");
                output.WriteLine($"    workspace: {Describe(skill.WorkspaceInstalled)}  {skill.WorkspacePath}");
                output.WriteLine($"    user:      {Describe(skill.UserInstalled)}  {skill.UserPath}");
            }
        }

        output.WriteLine();
        output.WriteLine(status.FullyOnboarded
            ? "Everything available is installed."
            : $"To install: {OnboardingStatus.InstallCommandHint} [--scope user] [--what hooks|skills|all]");

        return 0;
    }

    private static int RunInstall(string[] args, TextWriter output)
    {
        var root = ResolveRepositoryRoot(args);
        var scope = HooksCommand.ResolveScope(args);
        var components = ResolveComponents(args);
        var force = HasFlag(args, "--force");

        output.WriteLine($"Installing into {root} ({HookInstaller.ScopeName(scope)} scope).");

        if (components.Contains(Component.Hooks))
        {
            output.WriteLine();
            output.WriteLine("Hooks:");
            var installer = CreateHookInstaller(args);

            foreach (var client in ResolveClients(args))
            {
                var result = installer.Install(client, root, scope, force);
                output.WriteLine($"  [{HookInstaller.ClientName(client)}] {result.Message}");
            }
        }

        if (components.Contains(Component.Skills))
        {
            output.WriteLine();
            output.WriteLine("Skills:");

            foreach (var result in new SkillInstaller().Install(scope, root, GetOption(args, "--skill"), force))
            {
                output.WriteLine($"  [{result.SkillName}] {result.Message}");
            }
        }

        output.WriteLine();
        output.WriteLine("Restart your agent CLI (or reload the window) so it rescans hooks and skills.");

        if (scope is InstallScope.Workspace)
        {
            output.WriteLine("Commit the written files to share this setup with your team.");
        }

        output.WriteLine("Claude Code and Codex CLI ask for approval before running repo-local hooks the first time.");

        return 0;
    }

    private static int RunUninstall(string[] args, TextWriter output)
    {
        var root = ResolveRepositoryRoot(args);
        var scope = HooksCommand.ResolveScope(args);
        var components = ResolveComponents(args);

        if (components.Contains(Component.Hooks))
        {
            output.WriteLine("Hooks:");
            var installer = CreateHookInstaller(args);

            foreach (var client in ResolveClients(args))
            {
                var result = installer.Uninstall(client, root, scope);
                output.WriteLine($"  [{HookInstaller.ClientName(client)}] {result.Message}");
            }
        }

        if (components.Contains(Component.Skills))
        {
            output.WriteLine("Skills:");

            foreach (var result in new SkillInstaller().Uninstall(scope, root, GetOption(args, "--skill")))
            {
                output.WriteLine($"  [{result.SkillName}] {result.Message}");
            }
        }

        return 0;
    }

    private static string Describe(bool installed) => installed ? "installed    " : "not installed";

    private static OnboardingInspector CreateInspector(string[] args) =>
        new(CreateHookInstaller(args), new SkillInstaller());

    private static HookInstaller CreateHookInstaller(string[] args) =>
        new(GetOption(args, "--package-version") ?? HooksCommand.GetPackageVersion());

    private static IReadOnlyCollection<Component> ResolveComponents(string[] args)
    {
        var value = GetOption(args, "--what");

        if (string.IsNullOrWhiteSpace(value) || value.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return [Component.Hooks, Component.Skills];
        }

        var components = new List<Component>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "hook" or "hooks":
                    components.Add(Component.Hooks);
                    break;

                case "skill" or "skills":
                    components.Add(Component.Skills);
                    break;
            }
        }

        return components.Count > 0 ? components.Distinct().ToList() : [Component.Hooks, Component.Skills];
    }

    private static IReadOnlyList<HookClient> ResolveClients(string[] args)
    {
        var value = GetOption(args, "--client");

        if (string.IsNullOrWhiteSpace(value) || value.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return [HookClient.Claude, HookClient.Copilot, HookClient.Codex];
        }

        var clients = new List<HookClient>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var client = part.ToLowerInvariant() switch
            {
                "claude" or "claude-code" => (HookClient?)HookClient.Claude,
                "copilot" or "copilot-cli" or "github-copilot" or "vscode" => HookClient.Copilot,
                "codex" or "codex-cli" => HookClient.Codex,
                _ => null,
            };

            if (client is { } resolved && !clients.Contains(resolved))
            {
                clients.Add(resolved);
            }
        }

        return clients.Count > 0 ? clients : [HookClient.Claude, HookClient.Copilot, HookClient.Codex];
    }

    private static string ResolveRepositoryRoot(string[] args)
    {
        var configured = GetOption(args, "--path");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? Directory.GetCurrentDirectory() : configured);
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var current = args[index];

            if (current.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return index + 1 < args.Length ? args[index + 1] : null;
            }

            if (current.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return current[(name.Length + 1)..];
            }
        }

        return null;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(arg => arg.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static int WriteUnknown(string subCommand, TextWriter error)
    {
        error.WriteLine($"Unknown 'onboard' sub-command '{subCommand}'.");
        WriteUsage(error);
        return 1;
    }

    private static int WriteUsageAndSucceed(TextWriter output)
    {
        WriteUsage(output);
        return 0;
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine(
            """
            Usage: dotnet-efcore-mcp onboard [command] [options]

            Sets up the optional agent integrations that ship with this MCP server. Everything here
            is opt-in, and `status` (the default) only reads.

              Hooks   Tell the agent at session start that this server exists, and remind it to
                      validate EF Core LINQ with it after writing a query.
              Skills  Workflows the agent loads on demand, e.g. filing a usable GitHub issue about
                      this server without leaking your private code.

            Commands:
              status       Report what is available and what is installed (default).
              install      Install hooks and/or skills.
              uninstall    Remove what this tool installed, leaving unrelated config intact.

            Options:
              --scope <name>    workspace (default, committed with the repo) or user (~, all repos).
              --what <list>     hooks, skills, or all (default). Comma-separated.
              --client <name>   claude, copilot, codex, or all (default). Comma-separated.
              --skill <name>    Only act on the named skill (default: all bundled skills).
              --path <dir>      Repository root to act on (default: current directory).
              --force           Replace an existing dotnet-efcore-mcp install.

            Examples:
              dnx DotnetEfCoreMcp.Server --yes --prerelease -- onboard status
              dnx DotnetEfCoreMcp.Server --yes --prerelease -- onboard install
              dnx DotnetEfCoreMcp.Server --yes --prerelease -- onboard install --scope user
              dnx DotnetEfCoreMcp.Server --yes --prerelease -- onboard install --what skills
            """);
    }
}
