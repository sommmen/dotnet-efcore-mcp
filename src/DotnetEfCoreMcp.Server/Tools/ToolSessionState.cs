using DotNetAgentSurface.Core;

namespace DotnetEfCoreMcp.Server.Tools;

/// <summary>The <see cref="IAgentSession"/> keys that carry the intent of <c>load_assembly</c>,
/// <c>select_target</c> and <c>swap_connection</c> across CLI processes. Only names and paths are
/// stored; connection strings stay in configuration and user secrets.</summary>
public static class ToolSessionKeys
{
    /// <summary>Target name to assembly path, as registered by <c>load_assembly</c>.</summary>
    public const string Targets = "targets";

    /// <summary>The target selected as default by <c>select_target</c>.</summary>
    public const string DefaultTarget = "defaultTarget";

    /// <summary>The connection name made active by <c>swap_connection</c>.</summary>
    public const string ActiveConnection = "activeConnection";

    /// <summary>Whether the active connection was swapped to with <c>allowProduction</c>.</summary>
    public const string ActiveConnectionAllowProduction = "activeConnectionAllowProduction";

    /// <summary>The stable HMAC key for <c>run_query</c> cursors, used when none is configured.</summary>
    public const string CursorKey = "cursorKey";
}

internal static class ToolSessionState
{
    public static void RecordTarget(IAgentSession? session, string targetName, string assemblyPath)
    {
        if (session is null)
        {
            return;
        }

        var targets = session.Get<Dictionary<string, string>>(ToolSessionKeys.Targets) ?? [];
        targets[targetName] = assemblyPath;
        session.Set(ToolSessionKeys.Targets, targets);
    }

    public static void RecordDefaultTarget(IAgentSession? session, string targetName) =>
        session?.Set(ToolSessionKeys.DefaultTarget, targetName);

    public static void RecordActiveConnection(IAgentSession? session, string connectionName, bool allowProduction)
    {
        if (session is null)
        {
            return;
        }

        session.Set(ToolSessionKeys.ActiveConnection, connectionName);
        session.Set(ToolSessionKeys.ActiveConnectionAllowProduction, allowProduction);
    }

    public static string GetOrCreateCursorKey(IAgentSession session)
    {
        if (session.Get<string>(ToolSessionKeys.CursorKey) is { Length: > 0 } existing)
        {
            return existing;
        }

        var created = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        session.Set(ToolSessionKeys.CursorKey, created);
        return created;
    }
}
