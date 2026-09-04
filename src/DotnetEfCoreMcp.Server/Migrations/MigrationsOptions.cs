namespace DotnetEfCoreMcp.Server.Migrations;

/// <summary>Controls migration inspection and script generation. <see cref="Enabled"/> gates only
/// <c>generate_migration_script</c> - <c>list_migrations</c> is always available, read-only, and
/// never mutates the target database.</summary>
public sealed class MigrationsOptions
{
    /// <summary>Whether the <c>generate_migration_script</c> MCP tool is available. Defaults to
    /// false. Does not affect <c>list_migrations</c>.</summary>
    public bool Enabled { get; init; }

    /// <summary>Maximum number of characters returned in a generated migration script.</summary>
    public int MaxScriptLength { get; init; } = 100_000;

    /// <summary>Extra wall-clock margin added to the connection command timeout before cancellation.</summary>
    public TimeSpan CancellationMargin { get; init; } = TimeSpan.FromSeconds(5);
}
