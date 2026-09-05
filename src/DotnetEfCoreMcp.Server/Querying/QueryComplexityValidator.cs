using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>Bounds the shape of a caller-authored <c>run_query</c>/<c>preview_query_sql</c> C#
/// expression *before* it is handed to Roslyn compilation (and therefore before any provider
/// translation or database access can happen), by walking the parsed syntax tree and comparing it
/// against the configured <see cref="QueryExecutionOptions"/> caps. This is a purely syntactic,
/// AST-level check: it does not resolve symbols, run the compiler's semantic model, or execute
/// anything.</summary>
internal static class QueryComplexityValidator
{
    /// <summary>LINQ/EF Core query-operator method names counted toward
    /// <see cref="QueryExecutionOptions.MaxQueryOperators"/>. Deliberately excludes
    /// <c>Include</c>/<c>ThenInclude</c>, which are counted separately toward
    /// <see cref="QueryExecutionOptions.MaxIncludedCollectionItems"/>.</summary>
    private static readonly HashSet<string> QueryOperatorNames = new(StringComparer.Ordinal)
    {
        "Where", "Select", "SelectMany", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending",
        "Skip", "Take", "Distinct", "GroupBy", "Join", "GroupJoin", "Zip",
        "Concat", "Union", "Except", "Intersect",
        "Count", "LongCount", "Sum", "Average", "Min", "Max",
        "First", "FirstOrDefault", "Single", "SingleOrDefault", "Last", "LastOrDefault",
        "Any", "All", "Contains", "ElementAt", "ElementAtOrDefault",
        "ToList", "ToArray", "ToDictionary", "ToHashSet", "AsEnumerable", "AsQueryable",
        "AsNoTracking", "AsTracking", "Reverse", "Cast", "OfType",
    };

    private static readonly HashSet<string> IncludeOperatorNames = new(StringComparer.Ordinal)
    {
        "Include", "ThenInclude",
    };

    /// <summary>Parses <paramref name="query"/> the same way <c>UserQuerySourceGenerator</c> would
    /// (as a single expression, or - if that fails - as a statement block) and validates it against
    /// every configured complexity cap. Throws a sanitized <see cref="QueryExecutionException"/>
    /// naming only the exceeded limit and its configured maximum on the first violation found; never
    /// includes the query text itself. If the text cannot be parsed as either an expression or a
    /// statement block, validation is skipped silently - the subsequent Roslyn compilation step
    /// reports the syntax error with its own sanitized message.</summary>
    internal static void Validate(string query, QueryExecutionOptions options)
    {
        if (options.MaxExpressionNodes <= 0) throw new InvalidOperationException("Query execution option MaxExpressionNodes must be positive.");
        if (options.MaxExpressionDepth <= 0) throw new InvalidOperationException("Query execution option MaxExpressionDepth must be positive.");
        if (options.MaxQueryOperators <= 0) throw new InvalidOperationException("Query execution option MaxQueryOperators must be positive.");
        if (options.MaxIncludedCollectionItems <= 0) throw new InvalidOperationException("Query execution option MaxIncludedCollectionItems must be positive.");

        var root = TryParse(query);
        if (root is null) return;

        var nodeCount = 0;
        var operatorCount = 0;
        var includeCount = 0;
        var maxDepth = 0;

        // Use an explicit stack to walk the AST iteratively instead of recursively,
        // preventing stack overflow on deeply nested user-controlled syntax trees.
        var stack = new Stack<(SyntaxNode Node, int Depth)>();
        stack.Push((root, 1));

        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();

            nodeCount++;
            if (depth > maxDepth) maxDepth = depth;

            if (node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess })
            {
                var methodName = memberAccess.Name.Identifier.ValueText;
                if (IncludeOperatorNames.Contains(methodName)) includeCount++;
                else if (QueryOperatorNames.Contains(methodName)) operatorCount++;
            }

            // Push children onto the stack in reverse order to maintain left-to-right traversal
            // (since stack is LIFO, reversing ensures children are processed in the correct order).
            foreach (var child in node.ChildNodes().Reverse())
            {
                stack.Push((child, depth + 1));
            }
        }

        // Final validation after walk completes.
        if (nodeCount > options.MaxExpressionNodes)
            throw new QueryExecutionException($"The query expression contains {nodeCount} syntax nodes, exceeding the configured maximum of {options.MaxExpressionNodes} (MaxExpressionNodes).");
        if (maxDepth > options.MaxExpressionDepth)
            throw new QueryExecutionException($"The query expression has a nesting depth of {maxDepth}, exceeding the configured maximum of {options.MaxExpressionDepth} (MaxExpressionDepth).");
        if (operatorCount > options.MaxQueryOperators)
            throw new QueryExecutionException($"The query expression contains {operatorCount} query operators, exceeding the configured maximum of {options.MaxQueryOperators} (MaxQueryOperators).");
        if (includeCount > options.MaxIncludedCollectionItems)
            throw new QueryExecutionException($"The query expression contains {includeCount} Include/ThenInclude calls, exceeding the configured maximum of {options.MaxIncludedCollectionItems} (MaxIncludedCollectionItems).");
    }

    private static SyntaxNode? TryParse(string query)
    {
        var expression = SyntaxFactory.ParseExpression(query);
        if (!expression.ContainsDiagnostics && expression.FullSpan.End == query.Length)
        {
            return expression;
        }

        var block = SyntaxFactory.ParseStatement($"{{{query}}}") as BlockSyntax;
        return block is not null && !block.ContainsDiagnostics ? block : null;
    }
}
