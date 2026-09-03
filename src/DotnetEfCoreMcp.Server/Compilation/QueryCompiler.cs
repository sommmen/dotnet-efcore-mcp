using System.Reflection;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Querying;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotnetEfCoreMcp.Server.Compilation;

public sealed class QueryCompiler(QueryCompilationOptions options)
{
    internal async Task<CompiledQuery> CompileAsync(
        GeneratedUserQuerySource source,
        LoadedAssemblyHandle target,
        CancellationToken cancellationToken)
    {
        if (options.CompileTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException("Query compilation option CompileTimeoutSeconds must be positive.");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.CompileTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            return await Task.Run(() => Compile(source, target, linked.Token), linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
        {
            throw new QueryExecutionException($"Query compilation timed out after {options.CompileTimeoutSeconds}s.", ex);
        }
    }

    private CompiledQuery Compile(GeneratedUserQuerySource source, LoadedAssemblyHandle target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var syntaxTree = CSharpSyntaxTree.ParseText(source.Source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            $"DotnetEfCoreMcp.UserQuery.{Guid.NewGuid():N}",
            [syntaxTree],
            GetReferences(target),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: false, nullableContextOptions: NullableContextOptions.Disable));

        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        var emit = compilation.Emit(pe, pdb, cancellationToken: cancellationToken);
        if (!emit.Success)
        {
            var messages = emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => FormatDiagnostic(d, source))
                .Take(10)
                .ToArray();
            throw new QueryExecutionException($"The C# query could not be compiled: {string.Join(" ", messages)}");
        }

        EnsureNoSandboxedNamespaceUsage(compilation, syntaxTree, source);

        return new CompiledQuery(pe.ToArray(), pdb.ToArray());
    }

    /// <summary>Namespace prefixes that a user-authored query may never reference, even though the
    /// types are physically defined inside an always-required core assembly (see
    /// <c>BuiltInReferenceAssemblyNames</c>) and therefore cannot be kept out of the compilation by
    /// omitting an assembly reference. This is a semantic, symbol-level check: it walks every
    /// identifier resolved within the user-authored region of the generated source (i.e. excluding
    /// the compiler-generated wrapper scaffolding, tracked by
    /// <see cref="GeneratedUserQuerySource.QueryHeaderLineCount"/>) and rejects any symbol whose
    /// containing namespace starts with one of these prefixes. See
    /// <c>docs/development/roslyn-user-query.md</c> "Safety model".</summary>
    private static readonly string[] DisallowedNamespacePrefixes =
    [
        "System.IO",
        "System.Net",
        "System.Diagnostics",
        "System.Reflection.Emit",
        "System.Runtime.Loader",
        "System.Runtime.InteropServices",
        // System.Threading is allowed for async plumbing (Task, CancellationToken) but not for
        // direct thread/synchronization-primitive control; see AllowedSystemThreadingNamespaces.
        "System.Threading",
    ];

    /// <summary>Sub-namespaces of <c>System.Threading</c> that remain reachable from user-authored
    /// query code despite the broader <c>System.Threading</c> entry in
    /// <see cref="DisallowedNamespacePrefixes"/>, because the generated wrapper's own
    /// <c>SaveChangesAsync</c> overrides (and idiomatic LINQ-adjacent code) rely on them.</summary>
    private static readonly string[] AllowedSystemThreadingNamespaces =
    [
        "System.Threading.Tasks",
    ];

    private static void EnsureNoSandboxedNamespaceUsage(
        CSharpCompilation compilation,
        SyntaxTree syntaxTree,
        GeneratedUserQuerySource source)
    {
        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var root = syntaxTree.GetRoot();

        foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var line = name.GetLocation().GetLineSpan().StartLinePosition.Line;
            if (line < source.QueryHeaderLineCount)
            {
                // Compiler-generated wrapper scaffolding, not user-authored query text.
                continue;
            }

            var symbolInfo = semanticModel.GetSymbolInfo(name);
            foreach (var symbol in EnumerateCandidates(symbolInfo))
            {
                if (IsDisallowedNamespaceSymbol(symbol))
                {
                    var displayName = symbol.ToDisplayString();
                    throw new QueryExecutionException(
                        $"The C# query could not be compiled: it references '{displayName}', which is not permitted inside a query.");
                }
            }
        }
    }

    private static IEnumerable<ISymbol> EnumerateCandidates(SymbolInfo symbolInfo)
    {
        if (symbolInfo.Symbol is not null)
        {
            yield return symbolInfo.Symbol;
        }

        foreach (var candidate in symbolInfo.CandidateSymbols)
        {
            yield return candidate;
        }
    }

    private static bool IsDisallowedNamespaceSymbol(ISymbol symbol)
    {
        var containingNamespace = symbol.ContainingNamespace;
        if (containingNamespace is null || containingNamespace.IsGlobalNamespace)
        {
            return false;
        }

        var namespaceName = containingNamespace.ToDisplayString();
        foreach (var allowed in AllowedSystemThreadingNamespaces)
        {
            if (IsNamespaceOrDescendant(namespaceName, allowed))
            {
                return false;
            }
        }

        foreach (var disallowed in DisallowedNamespacePrefixes)
        {
            if (IsNamespaceOrDescendant(namespaceName, disallowed))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNamespaceOrDescendant(string namespaceName, string prefix) =>
        namespaceName.Equals(prefix, StringComparison.Ordinal) ||
        namespaceName.StartsWith(prefix + ".", StringComparison.Ordinal);

    /// <summary>The only shared-framework assemblies compiled queries may reference. This is
    /// deliberately a small allowlist rather than every assembly in TRUSTED_PLATFORM_ASSEMBLIES: it
    /// gives a query author enough of the BCL to write ordinary LINQ/EF Core code, while omitting
    /// APIs that would let a query escape the intended read/write-a-database sandbox, notably
    /// file/network I/O (<c>System.IO*</c>, <c>System.Net*</c>), process control
    /// (<c>System.Diagnostics.Process</c>), dynamic code generation/loading
    /// (<c>System.Reflection.Emit*</c>, <c>System.Runtime.Loader</c>), and most of
    /// <c>System.Threading</c>. Omission is enforced entirely by absence from the compiler's
    /// reference list: an identifier that resolves to a type in one of those assemblies simply
    /// fails to bind, producing an ordinary (sanitized) compile diagnostic rather than a runtime
    /// exception. See <c>docs/development/roslyn-user-query.md</c> "Safety model".</summary>
    private static readonly string[] BuiltInReferenceAssemblyNames =
    [
        "System.Private.CoreLib",
        "System.Runtime",
        "System.Linq",
        "System.Linq.Queryable",
        "System.Linq.Expressions",
        "System.Collections",
        "System.ObjectModel",
        // Required because EF Core's DbSet<T> implements System.ComponentModel.IListSource;
        // the compiler must bind that interface to resolve the target DbContext's base class,
        // even though user query code never references it directly.
        "System.ComponentModel.TypeConverter",
    ];

    private IEnumerable<MetadataReference> GetReferences(LoadedAssemblyHandle target)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var trustedPlatformAssemblies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            trustedPlatformAssemblies[Path.GetFileNameWithoutExtension(path)] = path;
        }

        foreach (var name in BuiltInReferenceAssemblyNames)
        {
            if (trustedPlatformAssemblies.TryGetValue(name, out var path))
            {
                paths.Add(path);
            }
        }

        paths.Add(target.AssemblyPath);
        foreach (var path in target.LoadedAssemblyPaths) paths.Add(path);
        AddAssemblyPath(typeof(QueryExecutionException).Assembly, paths);
        AddAssemblyPath(typeof(Microsoft.EntityFrameworkCore.DbContext).Assembly, paths);

        foreach (var name in options.AdditionalReferenceAssemblyNames)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a =>
                string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
            if (assembly is null || string.IsNullOrWhiteSpace(assembly.Location))
                throw new InvalidOperationException($"Configured query reference '{name}' is not loaded from a file.");
            paths.Add(assembly.Location);
        }

        return paths.Where(File.Exists).Select(path => MetadataReference.CreateFromFile(path));
    }

    private static void AddAssemblyPath(Assembly assembly, ISet<string> paths)
    {
        if (!string.IsNullOrWhiteSpace(assembly.Location)) paths.Add(assembly.Location);
    }

    private static string FormatDiagnostic(Diagnostic diagnostic, GeneratedUserQuerySource source)
    {
        var line = diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1 - source.QueryHeaderLineCount;
        return line > 0 ? $"Line {line}: {diagnostic.GetMessage()}" : diagnostic.GetMessage();
    }
}

internal sealed record CompiledQuery(byte[] Pe, byte[] Pdb);
