using DotNetAgentSurface.CommandLine;
using DotNetAgentSurface.Core;
using DotNetAgentSurface.Hosting;
using DotNetAgentSurface.Mcp;
using DotnetEfCoreMcp.Server.Hooks;
using DotnetEfCoreMcp.Server.Hosting;
using DotnetEfCoreMcp.Server.Onboarding;
using DotnetEfCoreMcp.Server.Tools;
using DotnetEfCoreMcp.Server.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotnetEfCoreMcp.Server.Cli;

/// <summary>Streams and state location for a run; tests override them, the process entry point uses the defaults.</summary>
public sealed record EfCoreSurfaceOptions
{
    public TextWriter? Output { get; init; }

    public TextWriter? Error { get; init; }

    public TextReader? Input { get; init; }

    public string? SessionStateDirectory { get; init; }

    public bool HandleCancelKeyPress { get; init; } = true;
}

/// <summary>Hosts the one tool set as an MCP server (no arguments) and as a plain CLI
/// (<c>dotnet-efcore-mcp cli &lt;operation&gt; ...</c>) on dotnet-agent-surface.</summary>
public static class EfCoreSurface
{
    public const string ExecutableName = "dotnet-efcore-mcp";

    public const string CliVerb = "cli";

    /// <summary>Overrides the per-user state root that holds CLI sessions. Windows ignores LOCALAPPDATA
    /// overrides, so a spawned process can only be isolated through this variable.</summary>
    internal const string SessionStateDirectoryVariable = "DOTNETEFCOREMCP_SESSION_STATE_DIR";

    private const string ConfigFlag = "--config";

    private static readonly CommandLineGlobalOption[] GlobalOptions =
    [
        new("--assembly", ConfigKey: "TargetAssemblyPath", Description: "Compiled target project DLL to load. Fails the command when it cannot be loaded."),
        new("--workspace", ConfigKey: "WorkspacePath", Description: "Directory to discover target assemblies in. Defaults to the current directory when --assembly is not given."),
        new("--target", InjectAs: "targetName", Description: "Named target assembly to use."),
        new("--connection", InjectAs: "connectionName", Description: "Logical connection name to use."),
        new(ConfigFlag, Description: "Configuration override as Key=Value, for example --config ToolOutput:Format=json. Repeatable."),
    ];

    public static Task<int> RunAsync(string[] args, EfCoreSurfaceOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new EfCoreSurfaceOptions();
        var isCli = args.Length > 0 && args[0].Equals(CliVerb, StringComparison.OrdinalIgnoreCase);

        Dictionary<string, string> configOverrides = new(StringComparer.OrdinalIgnoreCase);
        if (isCli && !TryExtractConfigOverrides(args, configOverrides, out args, out var configError))
        {
            (options.Error ?? Console.Error).WriteLine(configError);
            return Task.FromResult(2);
        }

        var effectiveArgs = args;
        return AgentSurfaceApplication.RunAsync(
            effectiveArgs,
            app => Configure(app, effectiveArgs, isCli, configOverrides, options),
            cancellationToken);
    }

    private static void Configure(
        AgentSurfaceApplication app,
        string[] args,
        bool isCli,
        Dictionary<string, string> configOverrides,
        EfCoreSurfaceOptions options)
    {
        app.ExecutableName = ExecutableName;
        app.CliVerb = CliVerb;
        app.McpVerb = null;
        app.DefaultSurface = AgentSurface.Mcp;
        app.SkillName = "dotnet-efcore-mcp-cli";
        app.SkillDescription = "Query an existing EF Core application's database and inspect its EF Core model with the dotnet-efcore-mcp CLI.";
        app.Output = options.Output;
        app.Error = options.Error;
        app.Input = options.Input;
        app.HandleCancelKeyPress = options.HandleCancelKeyPress;
        app.ConfigureSession(session =>
        {
            session.AppName = ExecutableName;
            session.StateDirectory = options.SessionStateDirectory ?? Environment.GetEnvironmentVariable(SessionStateDirectoryVariable);
        });

        foreach (var global in GlobalOptions)
        {
            app.AddGlobalOption(global.Name, global.InjectAs, global.ConfigKey, global.Description);
        }

        app.AddVerb(HooksCommand.VerbName, verbArgs => Task.FromResult(HooksCommand.Run([HooksCommand.VerbName, .. verbArgs], Console.Out, Console.Error, Console.In)));
        app.AddVerb(OnboardCommand.VerbName, verbArgs => Task.FromResult(OnboardCommand.Run([OnboardCommand.VerbName, .. verbArgs], Console.Out, Console.Error)));
        app.AddVerb(VersionCommand.VerbName, verbArgs => VersionCommand.RunAsync([VersionCommand.VerbName, .. verbArgs], Console.Out, Console.Error));

        if (isCli)
        {
            ConfigureCli(app, args, configOverrides);
        }
        else
        {
            app.ConfigureServices(builder =>
            {
                builder.Services.AddSingleton<IHostedService>(services => CreatePreloadService(services, failOnConfiguredAssemblyError: false));
                ServerComposition.AddEfCoreServices(builder);
            });
            app.ConfigureMcp(mcp => ServerComposition.AddEfCoreMcpServer(mcp.Services));
        }

        app.ConfigureCatalog((services, catalog) => catalog.AddMcpServerTools(
            typeof(EfCoreMcpTools).Assembly,
            targetFactory: type => services.GetRequiredService(type),
            catalogOptions =>
            {
                catalogOptions.SafetyMapping = McpToolSafetyMapping.None;
                catalogOptions.ResolveTargetsFromServices = true;
            }));
    }

    private static void ConfigureCli(AgentSurfaceApplication app, string[] args, Dictionary<string, string> configOverrides)
    {
        var cliArgs = args.Skip(1).ToArray();
        var globals = GlobalOptions.Append(new CommandLineGlobalOption("--session")).ToArray();
        var flagValues = OperationCommandLineAdapter.ReadConfigurationValues(cliArgs, globals);
        var explicitAssembly = flagValues.ContainsKey("TargetAssemblyPath");

        var start = OperationCommandLineAdapter.FindCommandStart(cliArgs, globals);
        var command = start < cliArgs.Length ? cliArgs[start] : null;
        var runsOperation = command is not null
            && command is not ("skill" or "session" or "--help" or "-h")
            && !cliArgs.Any(static arg => arg is "--help" or "-h");

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Logging:LogLevel:Default"] = "Warning",
        };
        foreach (var (key, value) in flagValues.Where(static pair => pair.Key is "TargetAssemblyPath" or "WorkspacePath"))
        {
            overrides[key] = value;
        }

        foreach (var (key, value) in configOverrides)
        {
            overrides[key] = value;
        }

        app.ConfigureServices(builder =>
        {
            if (runsOperation)
            {
                builder.Services.AddSingleton<IHostedService>(services => CreatePreloadService(services, failOnConfiguredAssemblyError: explicitAssembly, Directory.GetCurrentDirectory()));
            }

            ServerComposition.AddEfCoreServices(
                builder,
                includeHostedServices: false,
                cursorSigningKeyFactory: services => ToolSessionState.GetOrCreateCursorKey(services.GetRequiredService<IAgentSession>()),
                overrideConfiguration: configuration =>
                {
                    configuration.AddInMemoryCollection(overrides);

                    // A pool would spawn a persistent worker only to tear it down when the process exits.
                    if (string.Equals(configuration["QueryExecution:Mode"], "Pooled", StringComparison.OrdinalIgnoreCase))
                    {
                        configuration["QueryExecution:Mode"] = "Auto";
                    }
                });
            builder.Services.AddSingleton(ToolSurfaceHints.Cli);
            builder.Services.AddSingleton<IAgentSessionInitializer, EfCoreSessionInitializer>();
            builder.Services.AddSingleton<EfCoreMcpTools>();
        });
    }

    private static TargetPreloadService CreatePreloadService(IServiceProvider services, bool failOnConfiguredAssemblyError, string? implicitWorkspacePath = null) =>
        new(
            services,
            services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<ILoggerFactory>(),
            failOnConfiguredAssemblyError,
            implicitWorkspacePath);

    private static bool TryExtractConfigOverrides(
        string[] args,
        Dictionary<string, string> overrides,
        out string[] remaining,
        out string? error)
    {
        var kept = new List<string> { args[0] };
        error = null;
        for (var index = 1; index < args.Length; index++)
        {
            string? pair;
            if (args[index].Equals(ConfigFlag, StringComparison.OrdinalIgnoreCase))
            {
                pair = index + 1 < args.Length ? args[++index] : null;
            }
            else if (args[index].StartsWith(ConfigFlag + "=", StringComparison.OrdinalIgnoreCase))
            {
                pair = args[index][(ConfigFlag.Length + 1)..];
            }
            else
            {
                kept.Add(args[index]);
                continue;
            }

            var separator = pair?.IndexOf('=') ?? -1;
            if (separator <= 0)
            {
                remaining = args;
                error = "--config requires a Key=Value argument, for example --config ToolOutput:Format=json.";
                return false;
            }

            overrides[pair![..separator]] = pair[(separator + 1)..];
        }

        remaining = [.. kept];
        return true;
    }
}
