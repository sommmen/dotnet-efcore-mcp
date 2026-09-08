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
    /// <c>Include</c>/<c>ThenInclude</c>, which must use the structured
    /// <see cref="QueryRequest.Include"/> request parameter.</summary>
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

    /// <summary>Validates that a query string conforms to complexity caps: node count,
    /// expression depth, and query-operator count. Also rejects raw
    /// <c>Include</c>/<c>ThenInclude</c> calls in query text, directing callers to use the
    /// structured <see cref="QueryRequest.Include"/> parameter instead.
    /// Throws a sanitized <see cref="QueryExecutionException"/> naming only the violated limit
    /// and its configured maximum; never includes query text. If the text cannot be parsed as an
    /// expression or statement block, validation is skipped silently; the subsequent Roslyn
    /// compilation step reports the syntax error with its own message.</summary>
    /// <remarks>This is a purely syntactic, AST-level validation that runs before Roslyn
    /// compilation and database access, so errors are caught early.</remarks>
    internal static void Validate(string query, QueryExecutionOptions options)
    {
        if (options.MaxExpressionNodes <= 0) throw new QueryExecutionException("The server-configured MaxExpressionNodes value must be positive.");
        if (options.MaxExpressionDepth <= 0) throw new QueryExecutionException("The server-configured MaxExpressionDepth value must be positive.");
        if (options.MaxQueryOperators <= 0) throw new QueryExecutionException("The server-configured MaxQueryOperators value must be positive.");

        var root = TryParse(query);
        if (root is null) return;

        var nodeCount = 0;
        var operatorCount = 0;
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
                if (IncludeOperatorNames.Contains(methodName))
                {
                    throw new QueryExecutionException(
                        $"Raw `{methodName}()` calls are not allowed in query text. Use the structured `include` request parameter instead.");
                }

                if (QueryOperatorNames.Contains(methodName)) operatorCount++;
            }

            // Fail fast: if either nodes or depth have already exceeded their limits,
            // stop walking to avoid wasting CPU on adversarially large expressions.
            if (nodeCount > options.MaxExpressionNodes || depth > options.MaxExpressionDepth)
                break;

            // Push children onto the stack in reverse order to maintain
            // left-to-right processing when popping from the stack.
            foreach (var child in node.ChildNodes().Reverse())
            {
                stack.Push((child, depth + 1));
            }
        }

        if (nodeCount > options.MaxExpressionNodes)
            throw new QueryExecutionException($"Query syntax tree contains {nodeCount} nodes, exceeding the configured maximum of {options.MaxExpressionNodes} (MaxExpressionNodes).");
        if (maxDepth > options.MaxExpressionDepth)
            throw new QueryExecutionException($"Query syntax tree has maximum nesting depth of {maxDepth}, exceeding the configured maximum of {options.MaxExpressionDepth} (MaxExpressionDepth).");
        if (operatorCount > options.MaxQueryOperators)
            throw new QueryExecutionException($"Query contains {operatorCount} query operators, exceeding the configured maximum of {options.MaxQueryOperators} (MaxQueryOperators).");
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
