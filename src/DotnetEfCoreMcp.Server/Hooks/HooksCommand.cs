using System.Reflection;

namespace DotnetEfCoreMcp.Server.Hooks;

/// <summary>
/// The <c>hooks</c> CLI verb: installs the agent hooks that remind an agent to validate LINQ with
/// this MCP server, and serves as the entry point those hooks call back into.
/// </summary>
/// <remarks>
/// Hook handlers run as short-lived processes on every matching tool call, so this path avoids
/// building the generic host: it reads stdin, writes one JSON object, and exits.
/// </remarks>
public static class HooksCommand
{
    /// <summary>The verb that routes to this command.</summary>
    public const string VerbName = "hooks";

    /// <summary>
    /// Runs the command.
    /// </summary>
    /// <param name="args">Arguments including the leading <c>hooks</c> verb.</param>
    /// <param name="output">Destination for stdout; hook responses must go here alone.</param>
    /// <param name="error">Destination for diagnostics.</param>
    /// <param name="input">Source of the hook payload.</param>
    /// <returns>The process exit code.</returns>
    public static int Run(string[] args, TextWriter output, TextWriter error, TextReader input)
    {
        if (args.Length < 2)
        {
            WriteUsage(error);
            return 1;
        }

        var subCommand = args[1].ToLowerInvariant();

        return subCommand switch
        {
            "install" => RunInstall(args, output),
            "uninstall" => RunUninstall(args, output),
            "status" => RunStatus(args, output),

            // Invoked by the hooks themselves. These must never fail the agent's tool call, so they
            // always return 0 and fall back to an empty response.
            "remind" => RunRemind(args, output, input),
            "cleanup" => RunCleanup(output, input),
            "--help" or "-h" or "help" => WriteUsageAndSucceed(output),
            _ => WriteUnknown(subCommand, error),
        };
    }

    private static int RunRemind(string[] args, TextWriter output, TextReader input)
    {
        try
        {
            // The client only selects response details; defaulting keeps a misconfigured hook
            // useful rather than silent.
            var client = ParseClient(args) ?? HookClient.Claude;
            output.Write(new HookReminder().Evaluate(input.ReadToEnd(), client));
        }
        catch (Exception)
        {
            // A reminder is advisory; never let it disrupt the session that triggered it.
            output.Write("{}");
        }

        return 0;
    }

    private static int RunCleanup(TextWriter output, TextReader input)
    {
        try
        {
            output.Write(new HookReminder().Cleanup(input.ReadToEnd()));
        }
        catch (Exception)
        {
            output.Write("{}");
        }

        return 0;
    }

    private static int RunInstall(string[] args, TextWriter output)
    {
        var root = ResolveRepositoryRoot(args);
        var force = HasFlag(args, "--force");
        var installer = CreateInstaller(args);

        foreach (var client in ResolveClients(args))
        {
            var result = installer.Install(client, root, force);
            output.WriteLine($"[{HookInstaller.ClientName(client)}] {result.Message}");
        }

        output.WriteLine();
        output.WriteLine("Hooks remind the agent to validate LINQ with dotnet-efcore-mcp after it writes a query.");
        output.WriteLine("Claude Code and Codex CLI ask for approval before running repo-local hooks the first time.");

        return 0;
    }

    private static int RunUninstall(string[] args, TextWriter output)
    {
        var root = ResolveRepositoryRoot(args);
        var installer = CreateInstaller(args);

        foreach (var client in ResolveClients(args))
        {
            var result = installer.Uninstall(client, root);
            output.WriteLine($"[{HookInstaller.ClientName(client)}] {result.Message}");
        }

        return 0;
    }

    private static int RunStatus(string[] args, TextWriter output)
    {
        var root = ResolveRepositoryRoot(args);
        var installer = CreateInstaller(args);

        foreach (var client in ResolveClients(args))
        {
            var result = installer.Status(client, root);
            output.WriteLine($"[{HookInstaller.ClientName(client)}] {result.Message}: {result.FilePath}");
        }

        return 0;
    }

    private static HookInstaller CreateInstaller(string[] args)
    {
        var version = GetOption(args, "--package-version") ?? GetPackageVersion();
        return new HookInstaller(version);
    }

    /// <summary>
    /// Pins installed hooks to the running tool's version so a session cannot silently pick up a
    /// different reminder implementation mid-flight.
    /// </summary>
    /// <returns>
    /// The NuGet package version, or <see langword="null"/> to leave the command unpinned (which
    /// happens when running from a build without the packaging metadata, such as a local F5).
    /// </returns>
    internal static string? GetPackageVersion()
    {
        var assembly = typeof(HooksCommand).Assembly;

        // The build embeds the real $(PackageVersion); prefer it, because the informational version
        // does not reproduce Nerdbank.GitVersioning's package-version suffix verbatim.
        var packageVersion = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "NuGetPackageVersion")?.Value;

        if (!string.IsNullOrWhiteSpace(packageVersion))
        {
            return packageVersion;
        }

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return null;
        }

        // Strip the "+<commit>" build metadata, which is not part of a NuGet package version.
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? informational[..plus] : informational;
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
            if (TryParseClient(part, out var client) && !clients.Contains(client))
            {
                clients.Add(client);
            }
        }

        return clients.Count > 0 ? clients : [HookClient.Claude, HookClient.Copilot, HookClient.Codex];
    }

    private static HookClient? ParseClient(string[] args)
    {
        var value = GetOption(args, "--client");
        return TryParseClient(value, out var client) ? client : null;
    }

    private static bool TryParseClient(string? value, out HookClient client)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "claude" or "claude-code":
                client = HookClient.Claude;
                return true;

            // "vscode" is accepted because Copilot's hook format is shared with the VS Code
            // extension, and Serena-style configs name that client "vscode".
            case "copilot" or "copilot-cli" or "github-copilot" or "vscode":
                client = HookClient.Copilot;
                return true;

            case "codex" or "codex-cli":
                client = HookClient.Codex;
                return true;

            default:
                client = default;
                return false;
        }
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
        error.WriteLine($"Unknown 'hooks' sub-command '{subCommand}'.");
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
            Usage: dotnet-efcore-mcp hooks <command> [options]

            Installs agent hooks that remind the agent to validate EF Core LINQ with this MCP
            server after it writes a query, and to use schema discovery instead of reading
            entity source by hand.

            Commands:
              install      Write repo-local hook configuration.
              uninstall    Remove previously installed hook configuration.
              status       Report whether hooks are installed.

            Options:
              --client <name>   claude, copilot, codex, or all (default: all). Comma-separated.
              --path <dir>      Repository root to write into (default: current directory).
              --force           Overwrite an existing dotnet-efcore-mcp hook block.

            Files written:
              claude    .claude/settings.json
              copilot   .github/hooks/dotnet-efcore-mcp.json
              codex     .codex/hooks.json

            Example:
              dnx DotnetEfCoreMcp.Server --yes --prerelease -- hooks install --client all
            """);
    }
}
