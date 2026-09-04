using System.Reflection;
using DotnetEfCoreMcp.Server.AssemblyLoading;

namespace DotnetEfCoreMcp.Server.Tests.AssemblyLoading;

/// <summary>Regression coverage for the class of bug fixed by adding
/// Microsoft.Extensions.Logging.Abstractions and, later, Microsoft.Data.SqlClient and its own
/// dependency closure to <see cref="SharedFrameworkAssemblyNames"/> (see the type's doc comment).
///
/// Rather than hard-coding a check for the specific field/member that happened to trigger each
/// historical bug, this walks the *entire* public API surface reachable from every assembly in
/// <see cref="SharedFrameworkAssemblyNames.Value"/> - base types, interfaces, public field/property
/// types, and public method/constructor parameter and return types, recursively - using a
/// <see cref="MetadataLoadContext"/> so the inspected assemblies are never actually loaded into this
/// test process. If that closure ever reaches an assembly that is not itself in the shared list (and
/// is not part of the BCL, which every <see cref="System.Runtime.Loader.AssemblyLoadContext"/> already
/// shares implicitly), <see cref="TargetAssemblyLoadContext"/> and
/// <c>DotnetEfCoreMcp.Server.Compilation.CompiledQueryLoadContext</c> would silently load a second,
/// type-identity-incompatible copy of it, reproducing the original
/// MissingFieldException/MissingMethodException/TypeLoadException family of bugs for whatever new
/// public member introduced the gap. This is the "principled mechanism" that lets new gaps be caught
/// automatically instead of one at a time as each one is hit in production.</summary>
public sealed class SharedFrameworkAssemblyClosureTests
{
    /// <summary>Assembly simple names that are part of the .NET runtime/BCL itself. Every
    /// <see cref="System.Runtime.Loader.AssemblyLoadContext"/>, including the isolated ones this
    /// project creates, resolves these via the default/TPA binder rather than via
    /// <see cref="SharedFrameworkAssemblyNames"/>, so they are not required to appear in the shared
    /// list and are excluded from the closure check.</summary>
    private static bool IsFrameworkAssembly(string assemblyName) =>
        assemblyName.StartsWith("System", StringComparison.Ordinal)
        || assemblyName is "mscorlib" or "netstandard";

    /// <summary>Historically some shared assembly names (e.g. Microsoft.AspNetCore.Identity, which
    /// is a metapackage with no assembly of its own) never had a matching physical DLL to begin
    /// with. Such names are harmless - <see cref="TargetAssemblyLoadContext"/> only consults the set
    /// when an assembly with that exact name is actually requested - but they cannot be loaded into
    /// a <see cref="MetadataLoadContext"/> to have their own closure walked, so they are skipped as
    /// BFS roots (they can still be discovered as *leaves*, which would be a real bug: something
    /// depending on them).</summary>
    private static readonly HashSet<string> KnownNamesWithoutAPhysicalAssembly = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.AspNetCore.Identity",
    };

    [Fact]
    public void Value_PublicApiClosure_DoesNotReachAnyAssemblyOutsideTheSharedList()
    {
        string binDir = AppContext.BaseDirectory;
        string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var searchPaths = Directory.GetFiles(binDir, "*.dll")
            .Concat(Directory.GetFiles(runtimeDir, "*.dll"))
            .ToArray();

        using var loadContext = new MetadataLoadContext(new PathAssemblyResolver(searchPaths));

        var shared = SharedFrameworkAssemblyNames.Value;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(shared.Except(KnownNamesWithoutAPhysicalAssembly, StringComparer.OrdinalIgnoreCase));
        var leaks = new List<string>();

        while (queue.Count > 0)
        {
            string name = queue.Dequeue();
            if (!visited.Add(name))
            {
                continue;
            }

            string path = Path.Combine(binDir, name + ".dll");
            if (!File.Exists(path))
            {
                // Every current entry ships a physical DLL that lands next to the test output; if a
                // future addition doesn't, that is itself worth failing loudly on rather than
                // silently skipping, so the assembly must be resolvable via the search paths too.
                path = searchPaths.FirstOrDefault(p =>
                    string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(
                        $"Shared assembly '{name}' could not be located under '{binDir}' or the runtime " +
                        "directory to walk its public API closure. Either it is missing a test-time " +
                        "dependency that would make it resolvable, or it belongs in " +
                        $"{nameof(KnownNamesWithoutAPhysicalAssembly)} because it has no physical assembly.");
            }

            Assembly assembly = loadContext.LoadFromAssemblyPath(path);
            foreach (string dependency in CollectPublicApiAssemblyReferences(assembly))
            {
                if (IsFrameworkAssembly(dependency) || string.Equals(dependency, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!shared.Contains(dependency))
                {
                    leaks.Add($"{name} -> {dependency}");
                }
                else if (!visited.Contains(dependency) && !KnownNamesWithoutAPhysicalAssembly.Contains(dependency))
                {
                    queue.Enqueue(dependency);
                }
            }
        }

        Assert.True(leaks.Count == 0, "The following shared assemblies expose public members whose types " +
            "belong to an assembly missing from SharedFrameworkAssemblyNames.Value; add each distinct " +
            "target assembly there (this is what makes reflection over the affected member work rather " +
            "than throwing MissingFieldException/MissingMethodException/TypeLoadException):" +
            Environment.NewLine + string.Join(Environment.NewLine, leaks.Distinct().OrderBy(l => l, StringComparer.Ordinal)));
    }

    /// <summary>Returns the distinct simple names of assemblies referenced by <paramref name="assembly"/>'s
    /// exported types' base types, interfaces, public instance/static fields and properties, and
    /// public instance/static method and constructor parameter/return types (recursing into generic
    /// arguments and array/pointer/byref element types).</summary>
    private static HashSet<string> CollectPublicApiAssemblyReferences(Assembly assembly)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddType(Type? type)
        {
            if (type is null)
            {
                return;
            }

            if (type.HasElementType)
            {
                AddType(type.GetElementType());
                return;
            }

            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                foreach (Type argument in type.GetGenericArguments())
                {
                    AddType(argument);
                }
            }

            if (type.IsGenericParameter)
            {
                return;
            }

            string? name = type.Assembly.GetName().Name;
            if (name is not null)
            {
                result.Add(name);
            }
        }

        Type[] types;
        try
        {
            types = assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).ToArray()!;
        }

        const BindingFlags PublicDeclared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (Type type in types)
        {
            AddType(type.BaseType);
            foreach (Type @interface in type.GetInterfaces())
            {
                AddType(@interface);
            }

            foreach (FieldInfo field in type.GetFields(PublicDeclared))
            {
                AddType(field.FieldType);
            }

            foreach (PropertyInfo property in type.GetProperties(PublicDeclared))
            {
                AddType(property.PropertyType);
            }

            foreach (MethodInfo method in type.GetMethods(PublicDeclared))
            {
                if (method.IsSpecialName && (method.Name.StartsWith("get_", StringComparison.Ordinal) || method.Name.StartsWith("set_", StringComparison.Ordinal)))
                {
                    // Already covered via the property itself above.
                    continue;
                }

                AddType(method.ReturnType);
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    AddType(parameter.ParameterType);
                }
            }

            foreach (ConstructorInfo constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (ParameterInfo parameter in constructor.GetParameters())
                {
                    AddType(parameter.ParameterType);
                }
            }
        }

        return result;
    }
}
