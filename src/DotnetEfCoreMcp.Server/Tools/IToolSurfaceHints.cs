namespace DotnetEfCoreMcp.Server.Tools;

/// <summary>Surface-specific wording for errors that tell the caller how to fix a missing target or
/// connection. The MCP text is the original, byte-identical wording; the CLI names its flags.</summary>
public interface IToolSurfaceHints
{
    /// <summary>Error for a tool call made before any target assembly is loaded.</summary>
    string NoTargetLoaded { get; }

    /// <summary>Error for a tool call that needs a connection when none is active or given.</summary>
    string NoActiveConnection { get; }
}

/// <summary>The built-in <see cref="IToolSurfaceHints"/> implementations.</summary>
public static class ToolSurfaceHints
{
    /// <summary>Wording for the MCP server.</summary>
    public static IToolSurfaceHints Mcp { get; } = new McpHints();

    /// <summary>Wording for the plain CLI.</summary>
    public static IToolSurfaceHints Cli { get; } = new CliHints();

    private sealed class McpHints : IToolSurfaceHints
    {
        public string NoTargetLoaded =>
            "No target assembly is loaded yet. Call load_assembly with the path to a compiled target project's DLL first.";

        public string NoActiveConnection =>
            "No connection is active yet. Call swap_connection, or pass an explicit connectionName. (Connections available via list_connections; call reload_connections if one you configured is missing from it.)";
    }

    private sealed class CliHints : IToolSurfaceHints
    {
        public string NoTargetLoaded =>
            "No target assembly is loaded yet. Pass --assembly <path-to-dll> (or --workspace <dir>) to the command, " +
            "or run `load_assembly --assembly-path <dll>` once to remember a target for this session.";

        public string NoActiveConnection =>
            "No connection is active yet. Pass --connection <name>, or run `swap_connection --connection-name <name>` once to remember one for this session. " +
            "(Connections available via list_connections.)";
    }
}
