using System.Reflection;

namespace DotnetEfCoreMcp.Server.DbContextDiscovery;

/// <summary>Describes a <c>Microsoft.EntityFrameworkCore.DbContext</c>-derived type discovered in a
/// loaded target assembly.</summary>
public sealed record DbContextDescriptor(string Name, string? FullName, Type ClrType)
{
    /// <summary>Best-effort classification of how this context is expected to be constructed,
    /// purely informational (surfaced to the MCP client so it understands which construction path
    /// will be used / any caveats that apply to it). The actual activation logic in
    /// <see cref="DbContextActivator"/> re-derives this independently.</summary>
    public DbContextConstructionKind ConstructionKind { get; init; }
}

public enum DbContextConstructionKind
{
    /// <summary>Constructor accepting <c>DbContextOptions&lt;TContext&gt;</c> or the non-generic
    /// <c>DbContextOptions</c> - the server builds these options itself from the connection
    /// registry, so the connection string is fully server-controlled.</summary>
    OptionsConstructor,

    /// <summary>An <c>IDesignTimeDbContextFactory&lt;TContext&gt;</c> implementation was found and
    /// will be used. The server overrides whatever connection string the factory configures with
    /// the registry entry after construction.</summary>
    DesignTimeFactory,

    /// <summary>Only a parameterless constructor was found; the type is assumed to configure
    /// itself via <c>OnConfiguring</c>. The server overrides the connection string after
    /// construction.</summary>
    ParameterlessOnConfiguring,

    /// <summary>No supported construction path was found.</summary>
    Unsupported,
}
