using Microsoft.EntityFrameworkCore;

namespace ManyDbSetsApp;

/// <summary>Simulates a real-world context with a very large number of public DbSets (like OPG
/// Platform's CommerceDbContext, which exposes 136). Exercises that <c>QueryExecutor</c> only
/// registers DbSets actually mentioned in the query text as extra lambda parameters, rather than
/// registering all of them - see the comment above the <c>otherDbSets</c> filter in
/// <c>QueryExecutor.ExecuteAsyncCore</c> for why registering all of them would fail once the
/// total exceeds Dynamic LINQ's built-in delegate arities.</summary>
public class ManyDbSetsAppDbContext : DbContext
{
    public ManyDbSetsAppDbContext(DbContextOptions<ManyDbSetsAppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Widget> Widgets => Set<Widget>();

    public DbSet<Gadget> Gadgets => Set<Gadget>();

    public DbSet<Filler01> Filler01s => Set<Filler01>();
    public DbSet<Filler02> Filler02s => Set<Filler02>();
    public DbSet<Filler03> Filler03s => Set<Filler03>();
    public DbSet<Filler04> Filler04s => Set<Filler04>();
    public DbSet<Filler05> Filler05s => Set<Filler05>();
    public DbSet<Filler06> Filler06s => Set<Filler06>();
    public DbSet<Filler07> Filler07s => Set<Filler07>();
    public DbSet<Filler08> Filler08s => Set<Filler08>();
    public DbSet<Filler09> Filler09s => Set<Filler09>();
    public DbSet<Filler10> Filler10s => Set<Filler10>();
    public DbSet<Filler11> Filler11s => Set<Filler11>();
    public DbSet<Filler12> Filler12s => Set<Filler12>();
    public DbSet<Filler13> Filler13s => Set<Filler13>();
    public DbSet<Filler14> Filler14s => Set<Filler14>();
    public DbSet<Filler15> Filler15s => Set<Filler15>();
    public DbSet<Filler16> Filler16s => Set<Filler16>();
    public DbSet<Filler17> Filler17s => Set<Filler17>();
    public DbSet<Filler18> Filler18s => Set<Filler18>();
    public DbSet<Filler19> Filler19s => Set<Filler19>();
    public DbSet<Filler20> Filler20s => Set<Filler20>();
}
