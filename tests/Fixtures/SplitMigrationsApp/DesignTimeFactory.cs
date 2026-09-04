using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SplitContextApp;

namespace SplitMigrationsApp;

// Design-time factory used only by `dotnet ef migrations add` to scaffold the fixture migration
// below into this (separate) assembly via MigrationsAssembly(); not referenced by the server or by
// tests at runtime.
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<SplitDbContext>
{
    public SplitDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SplitDbContext>()
            .UseSqlite("Data Source=designtime.db", o => o.MigrationsAssembly(typeof(DesignTimeFactory).Assembly.GetName().Name))
            .Options;
        return new SplitDbContext(options);
    }
}
