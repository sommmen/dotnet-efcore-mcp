using System.Reflection;
using Microsoft.EntityFrameworkCore;
using DotnetEfCoreMcp.Server.Connections;

namespace DotnetEfCoreMcp.Server.DbContextDiscovery;

/// <summary>Constructs a <see cref="DbContext"/> instance for a discovered type, always using a
/// connection string resolved from the server-side <see cref="ConnectionRegistry"/> - never one
/// supplied by the caller/MCP client, and never the target project's own configured connection
/// string as-is.
///
/// Construction is attempted in this order:
/// <list type="number">
/// <item>A constructor accepting <c>DbContextOptions&lt;TContext&gt;</c> (the common ASP.NET Core
/// convention) - options are built entirely by the server, so the connection string is fully
/// server-controlled from the start.</item>
/// <item>A constructor accepting the non-generic <c>DbContextOptions</c>, built the same way.</item>
/// <item>An <c>IDesignTimeDbContextFactory&lt;TContext&gt;</c> implementation found in the same
/// assembly - the factory is invoked, then the connection string it configured is forcibly
/// replaced with the registry entry's value via <c>Database.SetConnectionString</c>.</item>
/// <item>A parameterless constructor (the type is assumed to configure itself entirely via
/// <c>OnConfiguring</c>) - again, the connection string is forcibly replaced afterwards.</item>
/// </list>
/// For the last two paths, overriding only replaces the connection string on whatever provider the
/// context already configured for itself; if that provider doesn't match the registry entry's
/// configured provider, the override will fail or behave unpredictably. This is a known MVP
/// limitation - contexts using those paths should be registered with a provider matching what
/// their own <c>OnConfiguring</c>/factory actually uses.</summary>
public static class DbContextActivator
{
    private const BindingFlags AnyInstanceCtor = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <param name="provider">The effective database provider to configure the context with -
    /// either the connection's explicitly configured provider or one inferred from the target
    /// assembly's EF Core provider package reference. Resolution happens before this call; this
    /// method never infers on its own.</param>
    public static DbContext CreateInstance(Type contextType, ConnectionRegistryEntry entry, DatabaseProvider provider)
    {
        var genericOptionsType = typeof(DbContextOptions<>).MakeGenericType(contextType);
        var genericOptionsCtor = contextType.GetConstructor(AnyInstanceCtor, binder: null, [genericOptionsType], modifiers: null);
        if (genericOptionsCtor is not null)
        {
            var options = CreateGenericOptions(contextType, entry, provider);
            return Invoke(genericOptionsCtor, contextType, [options]);
        }

        var nonGenericOptionsCtor = contextType.GetConstructor(AnyInstanceCtor, binder: null, [typeof(DbContextOptions)], modifiers: null);
        if (nonGenericOptionsCtor is not null)
        {
            var builder = new DbContextOptionsBuilder();
            ConfigureProvider(builder, entry, provider);
            return Invoke(nonGenericOptionsCtor, contextType, [builder.Options]);
        }

        var factoryType = DbContextScanner.FindDesignTimeFactoryType(contextType);
        if (factoryType is not null)
        {
            var factoryInterfaceType = typeof(Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<>).MakeGenericType(contextType);
            object factory;
            try
            {
                factory = Activator.CreateInstance(factoryType)
                    ?? throw new DbContextActivationException($"Design-time factory '{factoryType.FullName}' for '{contextType.FullName}' could not be instantiated.");
            }
            catch (Exception ex) when (ex is not DbContextActivationException)
            {
                throw new DbContextActivationException($"Design-time factory '{factoryType.FullName}' for '{contextType.FullName}' threw during construction.", ex);
            }

            var createMethod = factoryInterfaceType.GetMethod("CreateDbContext")!;
            DbContext instance;
            try
            {
                instance = (DbContext)createMethod.Invoke(factory, [Array.Empty<string>()])!;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw new DbContextActivationException($"Design-time factory for '{contextType.FullName}' threw while creating the context.", ex.InnerException);
            }

            OverrideConnectionString(instance, entry, contextType, provider);
            return instance;
        }

        var parameterlessCtor = contextType.GetConstructor(AnyInstanceCtor, binder: null, Type.EmptyTypes, modifiers: null);
        if (parameterlessCtor is not null)
        {
            var instance = Invoke(parameterlessCtor, contextType, null);
            OverrideConnectionString(instance, entry, contextType, provider);
            return instance;
        }

        throw new DbContextActivationException(
            $"DbContext type '{contextType.FullName}' has no supported construction path. Add a public constructor accepting DbContextOptions<{contextType.Name}> (recommended), implement IDesignTimeDbContextFactory<{contextType.Name}>, or add a parameterless constructor that configures itself via OnConfiguring.");
    }

    private static DbContext Invoke(ConstructorInfo constructor, Type contextType, object?[]? args)
    {
        try
        {
            return (DbContext)constructor.Invoke(args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new DbContextActivationException($"Constructing '{contextType.FullName}' threw an exception.", ex.InnerException);
        }
    }

    private static object CreateGenericOptions(Type contextType, ConnectionRegistryEntry entry, DatabaseProvider provider)
    {
        var builderType = typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType);
        var builder = (DbContextOptionsBuilder)Activator.CreateInstance(builderType)!;
        ConfigureProvider(builder, entry, provider);

        // DbContextOptionsBuilder<TContext> re-declares `Options` with `new` to narrow its return
        // type; without DeclaredOnly, GetProperty("Options") sees both the base and derived
        // members and throws AmbiguousMatchException. DeclaredOnly restricts the search to the
        // member declared directly on builderType, i.e. the narrowed, generically-typed one.
        var optionsProperty = builderType.GetProperty("Options", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
        return optionsProperty.GetValue(builder)!;
    }

    private static void ConfigureProvider(DbContextOptionsBuilder builder, ConnectionRegistryEntry entry, DatabaseProvider provider)
    {
        switch (provider)
        {
            case DatabaseProvider.Sqlite:
                builder.UseSqlite(entry.ConnectionString, o => o.CommandTimeout(entry.CommandTimeoutSeconds));
                break;
            case DatabaseProvider.SqlServer:
                builder.UseSqlServer(entry.ConnectionString, o => o.CommandTimeout(entry.CommandTimeoutSeconds));
                break;
            case DatabaseProvider.PostgreSql:
                builder.UseNpgsql(entry.ConnectionString, o => o.CommandTimeout(entry.CommandTimeoutSeconds));
                break;
            default:
                throw new DbContextActivationException($"Provider '{provider}' is not supported.");
        }
    }

    private static void OverrideConnectionString(DbContext instance, ConnectionRegistryEntry entry, Type contextType, DatabaseProvider provider)
    {
        try
        {
            instance.Database.SetConnectionString(entry.ConnectionString);
        }
        catch (Exception ex)
        {
            instance.Dispose();
            throw new DbContextActivationException(
                $"'{contextType.FullName}' could not be reconfigured to use the server-registered connection '{entry.Name}'. This usually means the context's own OnConfiguring/design-time factory uses a different database provider than the one registered ({provider}); register it with the matching provider.",
                ex);
        }
    }
}
