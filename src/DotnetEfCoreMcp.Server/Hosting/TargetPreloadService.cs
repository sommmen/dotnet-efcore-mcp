using DotNetAgentSurface.Core;
using DotNetAgentSurface.Hosting;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DotnetEfCoreMcp.Server.Hosting;

/// <summary>Runs <see cref="ServerComposition.PreloadTargets"/> when the host starts. It is registered
/// ahead of every other hosted service so targets are loaded before the reload watcher and the transport
/// start. It also runs for CLI invocations, where an explicit <c>--assembly</c> that cannot be loaded
/// fails the command instead of only being logged.</summary>
/// <param name="implicitWorkspacePath">Workspace to discover in when nothing else names a target and the
/// session remembers none. The CLI passes the current directory.</param>
public sealed class TargetPreloadService(
    IServiceProvider services,
    IConfiguration configuration,
    ILoggerFactory loggerFactory,
    bool failOnConfiguredAssemblyError = false,
    string? implicitWorkspacePath = null) : ICliHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            ServerComposition.PreloadTargets(
                services,
                ApplyImplicitWorkspace(),
                loggerFactory.CreateLogger("Program"),
                failOnConfiguredAssemblyError);
        }
        catch (AssemblyLoadFailedException ex) when (failOnConfiguredAssemblyError)
        {
            throw new InvalidOperationException($"Could not load the assembly given with --assembly: {ex.Message}", ex);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private IConfiguration ApplyImplicitWorkspace()
    {
        if (string.IsNullOrWhiteSpace(implicitWorkspacePath)
            || !string.IsNullOrWhiteSpace(configuration["TargetAssemblyPath"])
            || !string.IsNullOrWhiteSpace(configuration["WorkspacePath"])
            || configuration.GetSection("AssemblyLoader:Targets").GetChildren().Any())
        {
            return configuration;
        }

        // A target remembered by an earlier load_assembly beats guessing from the working directory.
        var session = services.GetService(typeof(IAgentSession)) as IAgentSession;
        if (session?.Get<Dictionary<string, string>>(ToolSessionKeys.Targets) is { Count: > 0 })
        {
            return configuration;
        }

        return new ConfigurationBuilder()
            .AddConfiguration(configuration)
            .AddInMemoryCollection([new KeyValuePair<string, string?>("WorkspacePath", implicitWorkspacePath)])
            .Build();
    }
}
