using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using System.Linq.Dynamic.Core;

namespace QueryHost;

/// <summary>
/// The out-of-process "query host". This process is launched by Launcher via
/// <c>dotnet exec --runtimeconfig &lt;TargetApp&gt;.runtimeconfig.json --depsfile &lt;TargetApp&gt;.deps.json QueryHost.dll ...</c>
/// (the same pattern <c>dotnet ef</c> uses to run its design-time tooling under a target
/// project's own runtime). It never references TargetApp.dll at compile time: it loads it from
/// a path given on the command line, finds the DbContext type and DbSet property by name via
/// reflection, and executes a caller-supplied predicate with System.Linq.Dynamic.Core so no
/// compile-time knowledge of the entity CLR type is required either.
///
/// All human-readable diagnostics go to stderr; stdout carries exactly one line of JSON so
/// Launcher can parse the result without scraping log text.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine(
                "usage: QueryHost <targetAssemblyPath> <dbContextTypeName> <dbSetPropertyName> <connectionString> <dynamicLinqPredicate>");
            return 2;
        }

        var targetAssemblyPath = args[0];
        var dbContextTypeName = args[1];
        var dbSetPropertyName = args[2];
        var connectionString = args[3];
        var predicate = args[4];

        Console.Error.WriteLine($"[QueryHost] pid={Environment.ProcessId} framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.Error.WriteLine($"[QueryHost] loading target assembly: {targetAssemblyPath}");

        try
        {
            var targetAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(targetAssemblyPath);

            var efCoreAssembly = typeof(DbContext).Assembly;
            Console.Error.WriteLine($"[QueryHost] Microsoft.EntityFrameworkCore resolved from: {efCoreAssembly.Location}");

            var contextType = targetAssembly.GetType(dbContextTypeName)
                ?? throw new InvalidOperationException($"Type '{dbContextTypeName}' not found in {targetAssembly.Location}.");

            using var context = CreateDbContext(contextType, connectionString);

            Console.Error.WriteLine("[QueryHost] ensuring database exists (EnsureCreated) ...");
            context.Database.EnsureCreated();

            var dbSetProperty = contextType.GetProperty(dbSetPropertyName)
                ?? throw new InvalidOperationException($"DbSet property '{dbSetPropertyName}' not found on '{dbContextTypeName}'.");
            var entityClrType = dbSetProperty.PropertyType.GetGenericArguments()[0];

            var queryable = (IQueryable)dbSetProperty.GetValue(context)!;

            SeedIfEmpty(context, queryable, entityClrType);

            Console.Error.WriteLine($"[QueryHost] executing dynamic LINQ predicate: Where(\"{predicate}\")");
            var filtered = queryable.Where(predicate);

            var rows = new List<Dictionary<string, object?>>();
            var entityProperties = entityClrType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var entity in filtered)
            {
                var row = new Dictionary<string, object?>();
                foreach (var prop in entityProperties)
                {
                    if (prop.GetIndexParameters().Length == 0)
                    {
                        row[prop.Name] = prop.GetValue(entity);
                    }
                }

                rows.Add(row);
            }

            var resultPayload = new
            {
                success = true,
                pid = Environment.ProcessId,
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                efCoreAssemblyLocation = efCoreAssembly.Location,
                rowCount = rows.Count,
                rows,
            };

            Console.Out.WriteLine(JsonSerializer.Serialize(resultPayload));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[QueryHost] ERROR: {ex}");
            var errorPayload = new { success = false, error = ex.Message };
            Console.Out.WriteLine(JsonSerializer.Serialize(errorPayload));
            return 1;
        }
    }

    private static DbContext CreateDbContext(Type contextType, string connectionString)
    {
        var builderType = typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType);
        var builder = (DbContextOptionsBuilder)Activator.CreateInstance(builderType)!;
        builder.UseSqlServer(connectionString);

        var optionsProperty = builderType.GetProperty("Options", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
        var options = optionsProperty.GetValue(builder)!;

        var ctor = contextType.GetConstructor([typeof(DbContextOptions<>).MakeGenericType(contextType)])
            ?? throw new InvalidOperationException($"'{contextType.FullName}' has no constructor accepting DbContextOptions<{contextType.Name}>.");
        return (DbContext)ctor.Invoke([options]);
    }

    private static void SeedIfEmpty(DbContext context, IQueryable queryable, Type entityClrType)
    {
        var any = queryable.Cast<object>().Any();
        if (any)
        {
            Console.Error.WriteLine("[QueryHost] table already has rows; skipping seed.");
            return;
        }

        Console.Error.WriteLine("[QueryHost] table is empty; seeding sample rows.");
        var seedRows = new (string Name, string Category, decimal Price)[]
        {
            ("Widget", "Hardware", 9.99m),
            ("Gadget", "Hardware", 49.99m),
            ("Gizmo", "Electronics", 129.50m),
            ("Doohickey", "Electronics", 19.25m),
            ("Contraption", "Hardware", 74.00m),
        };

        foreach (var seed in seedRows)
        {
            var instance = Activator.CreateInstance(entityClrType)!;
            entityClrType.GetProperty("Name")!.SetValue(instance, seed.Name);
            entityClrType.GetProperty("Category")!.SetValue(instance, seed.Category);
            entityClrType.GetProperty("Price")!.SetValue(instance, seed.Price);
            context.Add(instance);
        }

        context.SaveChanges();
    }
}
