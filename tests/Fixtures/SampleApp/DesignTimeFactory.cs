using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SampleApp;

// Design-time factory used only by `dotnet ef migrations add` to scaffold the fixture
// migration below; not referenced by the server or by tests at runtime.
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<SampleAppDbContext>
{
    public SampleAppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SampleAppDbContext>()
            .UseSqlite("Data Source=designtime.db")
            .Options;
        return new SampleAppDbContext(options);
    }
}
