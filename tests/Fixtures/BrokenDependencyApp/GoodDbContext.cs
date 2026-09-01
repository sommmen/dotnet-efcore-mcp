using Microsoft.EntityFrameworkCore;

namespace BrokenDependencyApp;

public class GoodDbContext(DbContextOptions<GoodDbContext> options) : DbContext(options)
{
}
