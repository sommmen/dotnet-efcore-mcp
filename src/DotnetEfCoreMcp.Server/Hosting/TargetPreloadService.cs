using DotNetAgentSurface.Hosting;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DotnetEfCoreMcp.Server.Hosting;

/// <summary>Runs <see cref="ServerComposition.PreloadTargets"/> when the host starts. It is registered
/// ahead of every other hosted service so targets are loaded before the reload watcher and the transport
/// start. It also runs for CLI invocations, where an explicit <c>--assembly</c> that cannot be loaded
/// fails the command instead of only being logged.</summary>
public sealed class TargetPreloadService(
    IServiceProvider services,
    IConfiguration configuration,
    ILoggerFactory loggerFactory,
    bool failOnConfiguredAssemblyError = false) : ICliHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            ServerComposition.PreloadTargets(
                services,
                configuration,
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
}
