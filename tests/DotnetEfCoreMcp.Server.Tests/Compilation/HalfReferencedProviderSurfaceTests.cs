using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.DbContextDiscovery;
using DotnetEfCoreMcp.Server.Tests.TestSupport;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DotnetEfCoreMcp.Server.Tests.Compilation;

/// <summary>Pins the premise behind <see cref="QueryCompilerProviderReferenceTests"/>: that
/// referencing an EF Core provider package without its underlying driver assembly really does
/// break an ordinary query. This reproduces issue #122's exact diagnostic directly, so if a future
/// EF Core or Npgsql release stops behaving this way the grouped-reference machinery is revealed
/// as unnecessary rather than quietly protecting against nothing.
///
/// <para>Deliberately builds its own compilation rather than going through
/// <see cref="QueryCompiler"/>: the whole point is to construct the half-referenced state the
/// compiler no longer produces.</para></summary>
public sealed class HalfReferencedProviderSurfaceTests
{
    [Fact]
    public void ReferencingAnEfCoreProviderWithoutItsDriver_BreaksAnOrdinaryQuery()
    {
        var handle = new AssemblyLoaderService().Load(FixturePaths.SampleAppDllPath);
        var contextType = DbContextScanner.FindDbContextTypes(handle.Assembly).Descriptors
            .Single(d => d.Name == "SampleAppDbContext").ClrType;
        var source = UserQuerySourceGenerator.Generate(
            contextType,
            "Customers.Where(c => new[] {\"A\",\"B\"}.Contains(c.Name)).Select(c => new { c.Name })",
            "halfreferenced1");

        var errors = CompileWithHalfReferencedNpgsql(handle, source);

        var cs0012 = Assert.Single(errors, d => d.Id == "CS0012");
        Assert.Contains("Npgsql", cs0012.GetMessage(), StringComparison.Ordinal);
        Assert.True(QueryCompiler.IsReferenceSetDiagnosticId(cs0012.Id));
    }

    private static Diagnostic[] CompileWithHalfReferencedNpgsql(LoadedAssemblyHandle handle, GeneratedUserQuerySource source)
    {
        // Mirrors QueryCompiler.BuiltInReferenceAssemblyNames rather than taking every TPA entry:
        // the test host's TPA set contains Npgsql.dll itself, which would silently repair the
        // half-referenced state this test exists to construct.
        string[] bcl =
        [
            "System.Private.CoreLib", "System.Runtime", "System.Linq", "System.Linq.Queryable",
            "System.Linq.Expressions", "System.Collections", "System.ObjectModel",
            "System.ComponentModel.TypeConverter",
        ];
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (bcl.Contains(Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)) paths.Add(path);
        }

        paths.Add(handle.AssemblyPath);
        foreach (var path in handle.LoadedAssemblyPaths) paths.Add(path);

        foreach (var name in new[]
        {
            "Microsoft.EntityFrameworkCore",
            "Microsoft.EntityFrameworkCore.Abstractions",
            "Microsoft.EntityFrameworkCore.Relational",
            // Npgsql.EntityFrameworkCore.PostgreSQL without Npgsql is the half-referenced state.
            "Npgsql.EntityFrameworkCore.PostgreSQL",
        })
        {
            var path = Path.Combine(AppContext.BaseDirectory, name + ".dll");
            if (File.Exists(path)) paths.Add(path);
        }

        var compilation = CSharpCompilation.Create(
            "DotnetEfCoreMcp.Tests.HalfReferenced",
            [CSharpSyntaxTree.ParseText(source.Source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest))],
            paths.Where(File.Exists).Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable));

        using var pe = new MemoryStream();
        return [.. compilation.Emit(pe).Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)];
    }
}
