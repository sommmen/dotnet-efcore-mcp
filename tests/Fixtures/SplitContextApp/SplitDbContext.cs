using Microsoft.EntityFrameworkCore;

namespace SplitContextApp;

/// <summary>DbContext for the split-assembly fixture pair. Its migrations live in the separate
/// SplitMigrationsApp assembly (see SplitMigrationsApp/Migrations), not here - this project has no
/// Migrations folder of its own.</summary>
public class SplitDbContext(DbContextOptions<SplitDbContext> options) : DbContext(options)
{
    public DbSet<Widget> Widgets => Set<Widget>();
}

public class Widget
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}
