namespace DotnetEfCoreMcp.Server.Mutations;

/// <summary>Controls whether destructive entity mutation MCP tools may run. They remain unavailable
/// for production and read-only connections even when this option is enabled.</summary>
public sealed class EntityMutationsOptions
{
    /// <summary>Gets whether entity mutation tools are enabled. Defaults to <see langword="false"/>.</summary>
    public bool Enabled { get; init; }
}
