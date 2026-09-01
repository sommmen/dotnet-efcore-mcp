using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DotnetEfCoreMcp.Server.DbContextDiscovery;

/// <summary>Scans a loaded assembly for concrete <see cref="DbContext"/>-derived types.</summary>
public static class DbContextScanner
{
    public static DbContextScanResult FindDbContextTypes(Assembly assembly)
    {
        Type[] types;
        IReadOnlyList<string> typeLoadWarnings;
        try
        {
            types = assembly.GetTypes();
            typeLoadWarnings = [];
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Some types in the assembly couldn't load (e.g. an optional dependency, such as an
            // ASP.NET Core shared-framework assembly, is missing). We can still work with the
            // types that DID load rather than failing discovery entirely, but the underlying
            // LoaderExceptions are the only clue as to *why* - including possibly why a
            // DbContext type itself (or one of its base types) failed to load. Surfacing these
            // instead of silently discarding them is essential: a caller who gets zero
            // discovered DbContexts needs to know whether that's because the assembly genuinely
            // has none, or because discovery was actually broken.
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            typeLoadWarnings = SummarizeLoaderExceptions(ex, types.Length);
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

        return new DbContextScanResult(results, typeLoadWarnings);
    }

    private static IReadOnlyList<string> SummarizeLoaderExceptions(ReflectionTypeLoadException ex, int loadedTypeCount)
    {
        var totalTypeCount = ex.Types.Length;
        var failedTypeCount = totalTypeCount - loadedTypeCount;

        // LoaderExceptions frequently contains the same FileNotFoundException/FileLoadException
        // repeated once per type that referenced the missing dependency; distinct messages keep
        // the warning readable instead of repeating the same line dozens of times.
        var distinctMessages = ex.LoaderExceptions
            .Where(e => e is not null)
            .Select(e => e!.Message)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var summary = $"{failedTypeCount} of {totalTypeCount} type(s) in the assembly failed to load " +
            "and were excluded from DbContext discovery. This can hide DbContext types that " +
            "depend on the missing types (directly or transitively) - for example, a shared " +
            "framework (e.g. Microsoft.AspNetCore.App) not being referenced, or a transitive " +
            "NuGet package DLL not being copied to the assembly's output folder.";

        var warnings = new List<string> { summary };
        warnings.AddRange(distinctMessages.Select(message => $"Type load error: {message}"));
        return warnings;
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
