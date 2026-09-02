using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SampleApp;

/// <summary>Regression fixture for the "ALC type identity split" bug: <see cref="OnConfiguring"/>
/// calls <see cref="RelationalDbContextOptionsBuilderExtensions"/>-style
/// <c>ConfigureWarnings</c> referencing <see cref="RelationalEventId.CommandExecuting"/>, an
/// <c>EventId</c>-typed static field defined in Microsoft.Extensions.Logging.Abstractions.
///
/// If that assembly (or Microsoft.Extensions.Logging) is missing from
/// <c>TargetAssemblyLoadContext.SharedAssemblyNames</c>, the target's isolated
/// <see cref="System.Runtime.Loader.AssemblyLoadContext"/> loads a second, type-incompatible copy
/// of it via <c>LoadAssemblyFromStream</c>. Simply constructing/initializing this context then
/// throws <see cref="MissingFieldException"/> deep inside EF Core, even though nothing about its
/// DbSets or queries differs from <see cref="SampleAppDbContext"/>. This mirrors the exact code
/// path that failed against the real-world OPG.Platform.Commerce.Core.DAL assembly (see the
/// server's regression test that loads this fixture).</summary>
public class WarningsConfiguredDbContext : DbContext
{
    public WarningsConfiguredDbContext(DbContextOptions<WarningsConfiguredDbContext> options)
        : base(options)
    {
    }

    public DbSet<Customer> Customers => Set<Customer>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.ConfigureWarnings(w => w.Log((RelationalEventId.CommandExecuting, LogLevel.Debug)));
    }
}
