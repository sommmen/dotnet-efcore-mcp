using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace NewerEfCoreApp;

/// <summary>A plain DbContext in a target that references a NEWER EF Core patch than the server.
/// Discovery should still find this type: nothing about it is unusual apart from the version of
/// the EF Core assembly its base class lives in.</summary>
public class CatalogDbContext : DbContext
{
    public CatalogDbContext(DbContextOptions<CatalogDbContext> options)
        : base(options)
    {
    }

    public DbSet<Product> Products => Set<Product>();
}

/// <summary>Same version situation, but the base class additionally reaches
/// Microsoft.AspNetCore.Identity.EntityFrameworkCore - another
/// <c>SharedFrameworkAssemblyNames</c> entry, and one that lives in the ASP.NET Core shared
/// framework rather than in a NuGet package the server restores itself.</summary>
public class AccountDbContext : IdentityDbContext<IdentityUser>
{
    public AccountDbContext(DbContextOptions<AccountDbContext> options)
        : base(options)
    {
    }

    public DbSet<Product> Products => Set<Product>();
}

public class Product
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public decimal Price { get; set; }
}
