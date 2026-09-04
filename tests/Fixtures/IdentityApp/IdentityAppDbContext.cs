using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace IdentityApp;

/// <summary>A minimal stand-in for OPG Platform's CommerceDbContext: an EF Core DbContext that
/// derives from <see cref="IdentityDbContext{TUser}"/>, whose base class reaches
/// Microsoft.AspNetCore.Identity.EntityFrameworkCore even though this fixture's own public
/// surface never mentions Identity types directly. This is the exact shape that broke
/// QueryCompiler.GetReferences for every single run_query call under the Roslyn engine, because
/// the reference was missing only for the inherited base class, not anything the query itself
/// touched.</summary>
public class IdentityAppDbContext : IdentityDbContext<IdentityUser>
{
    public IdentityAppDbContext(DbContextOptions<IdentityAppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Order> Orders => Set<Order>();
}

public class Order
{
    public int Id { get; set; }

    public decimal Total { get; set; }
}
