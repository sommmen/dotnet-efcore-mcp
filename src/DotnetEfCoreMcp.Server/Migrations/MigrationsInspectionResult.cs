namespace DotnetEfCoreMcp.Server.Migrations;

/// <summary>A migration known to the migration assembly, already applied to the target database
/// per <c>__EFMigrationsHistory</c>.</summary>
public sealed class AppliedMigrationInfo
{
    public required string MigrationId { get; init; }

    public required string ProductVersion { get; init; }
}

/// <summary>A migration known to the migration assembly that has not (or cannot be confirmed to
/// have) been applied to the target database.</summary>
public sealed class PendingMigrationInfo
{
    public required string MigrationId { get; init; }

    /// <summary>"{Provider}:{ProductVersion}" - the provider the connection targets and the EF
    /// Core product version the migration was authored against.</summary>
    public required string Target { get; init; }
}

/// <summary>The result of inspecting a <c>DbContext</c>'s known and applied migrations.</summary>
public sealed class MigrationsInspectionResult
{
    public required IReadOnlyList<AppliedMigrationInfo> AppliedMigrations { get; init; }

    public required IReadOnlyList<PendingMigrationInfo> PendingMigrations { get; init; }

    public required bool DatabaseExists { get; init; }

    public required bool AppliedStateAvailable { get; init; }
}
