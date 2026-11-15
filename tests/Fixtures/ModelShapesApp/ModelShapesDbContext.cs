using Microsoft.EntityFrameworkCore;

namespace ModelShapesApp;

public class ModelShapesDbContext(DbContextOptions<ModelShapesDbContext> options) : DbContext(options)
{
    public DbSet<Post> Posts => Set<Post>();

    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<ArchivedPostSummary> ArchivedPostSummaries => Set<ArchivedPostSummary>();

    public DbSet<Audit<int>> Audits => Set<Audit<int>>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Post>(builder =>
        {
            builder.HasMany(p => p.Tags).WithMany(t => t.Posts);
            builder.HasMany(p => p.Categories).WithMany(c => c.Posts);
        });

        modelBuilder.Entity<ArchivedPostSummary>().HasNoKey().ToView("ArchivedPostSummaries");
    }
}

public class Post
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Subtitle { get; set; }

    public List<Tag> Tags { get; set; } = [];

    public List<Category> Categories { get; set; } = [];
}

public class Tag
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<Post> Posts { get; set; } = [];
}

public class Category
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<Post> Posts { get; set; } = [];
}

public class ArchivedPostSummary
{
    public string? Title { get; set; }
}

public class Audit<TKey>
{
    public int Id { get; set; }

    public TKey? Subject { get; set; }
}
