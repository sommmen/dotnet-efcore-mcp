using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DotnetEfCoreMcp.Server.DbContextDiscovery;

/// <summary>Scans a loaded assembly for concrete <see cref="DbContext"/>-derived types.</summary>
public static class DbContextScanner
{
    public static IReadOnlyList<DbContextDescriptor> FindDbContextTypes(Assembly assembly)
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Some types in the assembly couldn't load (e.g. an optional dependency is missing).
            // We can still work with the types that DID load rather than failing discovery
            // entirely.
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }

        var results = new List<DbContextDescriptor>();

        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition)
            {
                continue;
            }

            if (!typeof(DbContext).IsAssignableFrom(type))
            {
                continue;
            }

            results.Add(new DbContextDescriptor(type.Name, type.FullName, type)
            {
                ConstructionKind = ClassifyConstructionKind(type),
            });
        }

        return results;
    }

    internal static DbContextConstructionKind ClassifyConstructionKind(Type contextType)
    {
        const BindingFlags anyInstanceCtor = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        var genericOptionsType = typeof(DbContextOptions<>).MakeGenericType(contextType);
        if (contextType.GetConstructor(anyInstanceCtor, binder: null, [genericOptionsType], modifiers: null) is not null)
        {
            return DbContextConstructionKind.OptionsConstructor;
        }

        if (contextType.GetConstructor(anyInstanceCtor, binder: null, [typeof(DbContextOptions)], modifiers: null) is not null)
        {
            return DbContextConstructionKind.OptionsConstructor;
        }

        if (FindDesignTimeFactoryType(contextType) is not null)
        {
            return DbContextConstructionKind.DesignTimeFactory;
        }

        if (contextType.GetConstructor(anyInstanceCtor, binder: null, Type.EmptyTypes, modifiers: null) is not null)
        {
            return DbContextConstructionKind.ParameterlessOnConfiguring;
        }

        return DbContextConstructionKind.Unsupported;
    }

    internal static Type? FindDesignTimeFactoryType(Type contextType)
    {
        var factoryInterfaceType = typeof(IDesignTimeDbContextFactory<>).MakeGenericType(contextType);

        Type[] candidateTypes;
        try
        {
            candidateTypes = contextType.Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            candidateTypes = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }

        return candidateTypes.FirstOrDefault(t =>
            !t.IsAbstract && !t.IsInterface && factoryInterfaceType.IsAssignableFrom(t));
    }
}
