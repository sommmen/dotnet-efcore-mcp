using DotNetAgentSurface.Core;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;
using DotnetEfCoreMcp.Server.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DotnetEfCoreMcp.Server.Cli;

/// <summary>Rebuilds the targets, default target and active connection that earlier CLI commands
/// recorded in the session, so <c>load_assembly</c> then <c>run_query</c> works across processes.</summary>
public sealed class EfCoreSessionInitializer(
    AssemblyLoaderService assemblyLoader,
    ConnectionRegistry connectionRegistry,
    IConfiguration configuration,
    ILogger<EfCoreSessionInitializer> logger) : IAgentSessionInitializer
{
    public ValueTask InitializeAsync(IAgentSession session, CancellationToken cancellationToken)
    {
        RestoreTargets(session);
        RestoreConnection(session);
        return ValueTask.CompletedTask;
    }

    private void RestoreTargets(IAgentSession session)
    {
        var targets = session.Get<Dictionary<string, string>>(ToolSessionKeys.Targets);
        if (targets is not { Count: > 0 })
        {
            return;
        }

        var alreadyLoaded = assemblyLoader.ListTargets().Select(target => target.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, path) in targets)
        {
            // Targets seeded from --assembly or configuration win over remembered ones.
            if (alreadyLoaded.Contains(name))
            {
                continue;
            }

            try
            {
                assemblyLoader.Load(path, name);
            }
            catch (AssemblyLoadFailedException ex)
            {
                logger.LogWarning("Could not restore target '{TargetName}' from session path '{Path}': {Reason}", name, path, ex.Message);
            }
        }

        var defaultTarget = session.Get<string>(ToolSessionKeys.DefaultTarget);
        if (string.IsNullOrWhiteSpace(defaultTarget) || !string.IsNullOrWhiteSpace(configuration["TargetAssemblyPath"]))
        {
            return;
        }

        try
        {
            assemblyLoader.SelectDefault(defaultTarget);
        }
        catch (UnknownAssemblyTargetException ex)
        {
            logger.LogWarning("Could not restore default target '{TargetName}' from the session: {Reason}", defaultTarget, ex.Message);
        }
    }

    private void RestoreConnection(IAgentSession session)
    {
        var connection = session.Get<string>(ToolSessionKeys.ActiveConnection);
        if (string.IsNullOrWhiteSpace(connection))
        {
            return;
        }

        try
        {
            connectionRegistry.SetActive(connection, session.Get<bool>(ToolSessionKeys.ActiveConnectionAllowProduction));
        }
        catch (Exception ex) when (ex is UnknownConnectionException or ProductionProtectedException)
        {
            logger.LogWarning("Could not restore active connection '{ConnectionName}' from the session: {Reason}", connection, ex.Message);
        }
    }
}
