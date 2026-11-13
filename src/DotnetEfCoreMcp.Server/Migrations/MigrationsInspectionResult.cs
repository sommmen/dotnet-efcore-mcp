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

    /// <summary>True when this pending migration's ID sorts (ordinally, as EF Core orders
    /// migrations) before <see cref="MigrationsInspectionResult.LastAppliedMigration"/> - typically
    /// a migration merged in from an older branch.</summary>
    public required bool OutOfOrder { get; init; }
}

/// <summary>A migration known to the migration assembly, in EF Core migration order, with its
/// applied state - the equivalent of one line of <c>dotnet ef migrations list</c>.</summary>
public sealed class KnownMigrationInfo
{
    public required string MigrationId { get; init; }

    /// <summary><c>null</c> when applied state is unavailable (database unreachable or
    /// <c>__EFMigrationsHistory</c> unreadable), so metadata is never presented as applied state.</summary>
    public required bool? Applied { get; init; }
}

/// <summary>The result of inspecting a <c>DbContext</c>'s known and applied migrations.</summary>
public sealed class MigrationsInspectionResult
{
    public required IReadOnlyList<AppliedMigrationInfo> AppliedMigrations { get; init; }

    public required IReadOnlyList<PendingMigrationInfo> PendingMigrations { get; init; }

    public required bool DatabaseExists { get; init; }

    public required bool AppliedStateAvailable { get; init; }

    public required IReadOnlyList<KnownMigrationInfo> Migrations { get; init; }

    public required int KnownMigrationCount { get; init; }

    /// <summary>The last row of the applied history, in history order; <c>null</c> when nothing is
    /// applied or applied state is unavailable.</summary>
    public required string? LastAppliedMigration { get; init; }

    /// <summary>Applied migration IDs that the migrations assembly doesn't know - the database is
    /// ahead of, or diverged from, the loaded code.</summary>
    public required IReadOnlyList<string> UnknownAppliedMigrations { get; init; }

    /// <summary>Whether the model differs from the latest migration snapshot; <c>null</c> when it
    /// couldn't be computed.</summary>
    public required bool? HasPendingModelChanges { get; init; }
}
