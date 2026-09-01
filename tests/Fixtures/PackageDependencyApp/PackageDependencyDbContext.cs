using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;

namespace PackageDependencyApp;

/// <summary>A DbContext whose surface deliberately touches a NuGet package (Newtonsoft.Json) and
/// the ASP.NET Core shared framework, so discovering it is only possible once both can be
/// resolved for the target assembly.</summary>
public class PackageDependencyDbContext : DbContext
{
    public PackageDependencyDbContext(DbContextOptions<PackageDependencyDbContext> options)
        : base(options)
    {
    }

    public DbSet<Document> Documents => Set<Document>();

    public JObject DescribeAsJson() => new() { ["name"] = nameof(PackageDependencyDbContext) };

    public static string? ReadTenant(HttpContext context) => context.Request.Headers["X-Tenant"];
}
