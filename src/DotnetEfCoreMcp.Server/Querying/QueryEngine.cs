namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>Selects which engine <c>run_query</c> uses to execute a caller-supplied query
/// expression. See <c>docs/development/roslyn-user-query.md</c> for the migration plan from
/// <see cref="DynamicLinq"/> to <see cref="Roslyn"/>.</summary>
public enum QueryEngine
{
    /// <summary>Parses <c>query</c> as a <c>System.Linq.Dynamic.Core</c> expression string against
    /// an allowlist of supported LINQ operators. The original engine; cannot support
    /// <c>Join</c>/<c>GroupJoin</c>/<c>SelectMany</c>/<c>Zip</c> or multi-statement queries.</summary>
    DynamicLinq,

    /// <summary>Compiles <c>query</c> as real C# (an expression or a small statement block) with
    /// <c>Microsoft.CodeAnalysis.CSharp</c>, wrapped in a generated <c>TContext</c> subclass and
    /// executed in a dedicated, collectible <see cref="System.Runtime.Loader.AssemblyLoadContext"/>.
    /// Supports the full LINQ surface, matching LINQPad's own query model.</summary>
    Roslyn,
}
