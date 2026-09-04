namespace DotnetEfCoreMcp.Server.Migrations;

/// <summary>Parameters for generating a migration script preview.</summary>
public sealed class MigrationScriptRequest
{
    /// <summary>Migration ID to script from (exclusive). Null/"0" means from the beginning of history.</summary>
    public string? FromMigration { get; init; }

    /// <summary>Migration ID to script to (inclusive). Null means the latest known migration.</summary>
    public string? ToMigration { get; init; }

    /// <summary>Whether the generated script guards each step against having already been applied.</summary>
    public bool Idempotent { get; init; } = true;
}

/// <summary>The result of generating a migration script preview. The script is never executed.</summary>
public sealed class MigrationScriptResult
{
    public required string Sql { get; init; }

    public required bool Truncated { get; init; }

    public required int MigrationCount { get; init; }
}
