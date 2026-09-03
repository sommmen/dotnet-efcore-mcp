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
/// <summary>Identifies which of <see cref="DbContextActivator"/>'s four supported construction
/// paths a target <see cref="DbContext"/> type uses, distinguishing the generic- vs. non-generic-
/// options constructor cases that <see cref="DbContextDiscovery.DbContextConstructionKind"/>
/// intentionally collapses into a single <c>OptionsConstructor</c> value for MCP client reporting.
/// This finer split matters to callers that need to *emit code* for a different, related type
/// (e.g. a Roslyn-compiled <c>UserQuery_{token} : TContext</c> subclass - see
/// docs/development/roslyn-user-query.md), because the constructor parameter type they must
/// generate/pass differs between the two: <c>DbContextOptions&lt;TContext&gt;</c> vs. the
/// non-generic <c>DbContextOptions</c>.</summary>
public enum DbContextConstructorShape
{
    /// <summary>Constructor accepting <c>DbContextOptions&lt;TContext&gt;</c>.</summary>
    GenericOptions,

    /// <summary>Constructor accepting the non-generic <c>DbContextOptions</c>.</summary>
    NonGenericOptions,

    /// <summary>No options-accepting constructor, but an <c>IDesignTimeDbContextFactory&lt;TContext&gt;</c>
    /// implementation exists alongside the context.</summary>
    DesignTimeFactory,

    /// <summary>Only a parameterless constructor exists; the context configures itself via
    /// <c>OnConfiguring</c>.</summary>
    Parameterless,

    /// <summary>None of the above construction paths are available.</summary>
    Unsupported,
}

public static class DbContextActivator
{
    private const BindingFlags AnyInstanceCtor = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <param name="provider">The effective database provider to configure the context with -
    /// either the connection's explicitly configured provider or one inferred from the target
    /// assembly's EF Core provider package reference. Resolution happens before this call; this
    /// method never infers on its own.</param>
    public static DbContext CreateInstance(Type contextType, ConnectionRegistryEntry entry, DatabaseProvider provider)
    {
        var kind = DetermineConstructorShape(contextType);
        if (kind == DbContextConstructorShape.GenericOptions)
        {
            var genericOptionsCtor = contextType.GetConstructor(AnyInstanceCtor, binder: null, [typeof(DbContextOptions<>).MakeGenericType(contextType)], modifiers: null)!;
            var options = CreateGenericOptions(contextType, entry, provider);
            return Invoke(genericOptionsCtor, contextType, [options]);
        }

        if (kind == DbContextConstructorShape.NonGenericOptions)
        {
            var nonGenericOptionsCtor = contextType.GetConstructor(AnyInstanceCtor, binder: null, [typeof(DbContextOptions)], modifiers: null)!;
            var options = BuildOptions(contextType, entry, provider);
            return Invoke(nonGenericOptionsCtor, contextType, [options]);
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

    /// <summary>Builds a <c>DbContextOptions&lt;TContext&gt;</c> instance (boxed as <see cref="object"/>
    /// since <paramref name="contextType"/> is only known at runtime) for <paramref name="contextType"/>.
    /// Public so callers building a *different* type that requires the same generically-typed options
    /// (e.g. a Roslyn-compiled <c>UserQuery_{token} : TContext</c> class emitting a constructor
    /// parameter typed as <c>DbContextOptions&lt;TContext&gt;</c> - see
    /// docs/development/roslyn-user-query.md) can reuse this instead of duplicating the
    /// <see cref="BindingFlags.DeclaredOnly"/> reflection dance below. Only valid when
    /// <see cref="DetermineConstructorShape"/> returns <see cref="DbContextConstructorShape.GenericOptions"/>
    /// - for <see cref="DbContextConstructorShape.NonGenericOptions"/> use <see cref="BuildOptions"/>
    /// instead, which returns the non-generic type that shape's constructor actually requires.</summary>
    /// <param name="configureAdditional">Optional extra configuration applied after the provider is
    /// set up, e.g. <c>b => b.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)</c>.</param>
    public static object CreateGenericOptions(Type contextType, ConnectionRegistryEntry entry, DatabaseProvider provider, Action<DbContextOptionsBuilder>? configureAdditional = null)
    {
        var builderType = typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType);
        var builder = (DbContextOptionsBuilder)Activator.CreateInstance(builderType)!;
        ConfigureProvider(builder, entry, provider);
        configureAdditional?.Invoke(builder);

        // DbContextOptionsBuilder<TContext> re-declares `Options` with `new` to narrow its return
        // type; without DeclaredOnly, GetProperty("Options") sees both the base and derived
        // members and throws AmbiguousMatchException. DeclaredOnly restricts the search to the
        // member declared directly on builderType, i.e. the narrowed, generically-typed one.
        var optionsProperty = builderType.GetProperty("Options", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
        return optionsProperty.GetValue(builder)!;
    }

    /// <summary>Determines which of the four construction paths a target <see cref="DbContext"/>
    /// type supports, without actually constructing it. Used by callers (e.g. the Roslyn-compiled
    /// query engine, see docs/development/roslyn-user-query.md) that need to know, ahead of time,
    /// what constructor shape to generate/invoke for a type they don't control - mirrors the same
    /// probing order <see cref="CreateInstance"/> itself uses.</summary>
    public static DbContextConstructorShape DetermineConstructorShape(Type contextType)
    {
        if (contextType.GetConstructor(AnyInstanceCtor, binder: null, [typeof(DbContextOptions<>).MakeGenericType(contextType)], modifiers: null) is not null)
        {
            return DbContextConstructorShape.GenericOptions;
        }

        if (contextType.GetConstructor(AnyInstanceCtor, binder: null, [typeof(DbContextOptions)], modifiers: null) is not null)
        {
            return DbContextConstructorShape.NonGenericOptions;
        }

        if (DbContextScanner.FindDesignTimeFactoryType(contextType) is not null)
        {
            return DbContextConstructorShape.DesignTimeFactory;
        }

        if (contextType.GetConstructor(AnyInstanceCtor, binder: null, Type.EmptyTypes, modifiers: null) is not null)
        {
            return DbContextConstructorShape.Parameterless;
        }

        return DbContextConstructorShape.Unsupported;
    }

    /// <summary>Builds a server-controlled, non-generic <see cref="DbContextOptions"/> instance for
    /// <paramref name="contextType"/>, the same way <see cref="CreateInstance"/> does for its
    /// <see cref="DbContextConstructorShape.NonGenericOptions"/> path. Exposed so callers that
    /// construct a *different* type deriving from <paramref name="contextType"/> (e.g. a
    /// Roslyn-compiled <c>UserQuery_{token} : TContext</c> class - see
    /// docs/development/roslyn-user-query.md) can reuse the exact same provider-configuration logic
    /// instead of duplicating <see cref="ConfigureProvider"/>'s provider switch. Only valid for
    /// context types whose <see cref="DetermineConstructorShape"/> is
    /// <see cref="DbContextConstructorShape.NonGenericOptions"/> - callers must check that first;
    /// for <see cref="DbContextConstructorShape.GenericOptions"/> use <see cref="CreateGenericOptions"/>
    /// instead, and design-time-factory / parameterless contexts configure themselves and have no
    /// server-built options to layer <paramref name="configureAdditional"/> onto.</summary>
    /// <param name="configureAdditional">Optional extra configuration applied after the provider is
    /// set up, e.g. <c>b => b.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)</c>.</param>
    public static DbContextOptions BuildOptions(Type contextType, ConnectionRegistryEntry entry, DatabaseProvider provider, Action<DbContextOptionsBuilder>? configureAdditional = null)
    {
        var builder = new DbContextOptionsBuilder();
        ConfigureProvider(builder, entry, provider);
        configureAdditional?.Invoke(builder);
        return builder.Options;
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

    /// <summary>Forces <paramref name="instance"/> to use the server-registered connection string,
    /// overriding whatever provider/connection its own construction path (design-time factory or
    /// parameterless constructor + OnConfiguring) set up. Internal (not private) so <see
    /// cref="Querying.RoslynQueryExecutor"/> can apply the same override to its Roslyn-compiled
    /// <c>UserQuery_{token}</c> subclasses of parameterless-shape contexts, which construct via
    /// <see cref="Activator.CreateInstance(Type, object?[]?)"/> directly rather than through this
    /// class's <see cref="CreateInstance"/>.</summary>
    internal static void OverrideConnectionString(DbContext instance, ConnectionRegistryEntry entry, Type contextType, DatabaseProvider provider)
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
