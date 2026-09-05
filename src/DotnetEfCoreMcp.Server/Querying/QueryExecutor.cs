using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>Shared helpers used by the Roslyn/LINQPad-style query execution engine (<see cref="RoslynQueryExecutor"/>)
/// and by <c>EfCoreMcpTools.RunQueryCore</c> for pre-execution access-policy enforcement.</summary>
public static class QueryExecutor
{
    private static readonly MethodInfo MaterializeMethod = typeof(QueryExecutor).GetMethod(nameof(MaterializeAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>Trims and validates a raw LINQPad-style query and extracts the leading DbSet property
    /// name it is rooted at. Made <c>internal</c> (rather than <c>private</c>) so
    /// <c>EfCoreMcpTools.RunQueryCore</c> can extract the same root name to enforce entity-level
    /// access policy (P0 #9) before the Roslyn engine compiles and executes the query, without
    /// duplicating this parsing logic.
    /// 
    /// IMPORTANT: This method enforces expression-mode only (rejects statements with ';'). Statement-mode
    /// queries are not supported for policy enforcement reasons; see P0 #9. The parsed root name is later
    /// validated by <see cref="TryGetDbSetEntityType"/> to ensure it actually refers to a DbSet property,
    /// preventing policy bypass via DbContext.Set<T>(), Database.ExecuteSqlRaw(), or other non-DbSet access.</summary>
    internal static (string Root, string Expression) NormalizeAndGetRoot(string? query, int maxQueryLength)
    {
        var value = query?.Trim();
        if (maxQueryLength <= 0) throw new InvalidOperationException("Query execution option MaxQueryLength must be positive.");
        if (value is { Length: > 0 } && value.Length > maxQueryLength)
            throw new QueryExecutionException("`query` exceeds the configured maximum length.");
        if (string.IsNullOrWhiteSpace(value)) throw new QueryExecutionException("`query` must be a non-empty LINQ expression.");
        if (value.EndsWith(';')) value = value[..^1].TrimEnd();
        if (value.Contains(';')) throw new QueryExecutionException("`query` must contain one expression only (statement-mode is not supported).");
        var match = System.Text.RegularExpressions.Regex.Match(value, "^(?<root>[A-Za-z_][A-Za-z0-9_]*)");
        if (!match.Success) throw new QueryExecutionException("`query` must start with a DbSet property name.");
        return (match.Groups["root"].Value, value);
    }

    /// <summary>
    /// Resolves the EF entity CLR type names that a raw query text may touch, so callers can enforce
    /// entity-level access policy (P0 #9) using the same names the policy is configured with (entity type
    /// names, e.g. <c>"Customer"</c>) rather than the unrelated DbSet property name the query is written
    /// against (e.g. <c>"Customers"</c>). This is a <see cref="Type"/>-based counterpart of the
    /// root-resolution logic the Roslyn engine performs against a live <see cref="DbContext"/> instance,
    /// letting policy be enforced before context construction. The root DbSet is resolved first (if it
    /// does not map to a public <c>DbSet&lt;T&gt;</c>
    /// property, its raw name is returned unchanged so an unresolvable/bogus root is still rejected through
    /// the same policy check rather than silently skipped); every other public <c>DbSet&lt;T&gt;</c> property
    /// whose name appears as a whole word in <paramref name="expressionText"/> (e.g. via
    /// <c>Union</c>/<c>Concat</c>/<c>Except</c>/<c>Intersect</c>) contributes its entity type name too.
    /// </summary>
    /// <remarks>Detection is text-based: it scans for DbSet property names using word-boundary regex
    /// matching on the raw expression text. LIMITATION: this can be bypassed via unicode escapes in
    /// identifiers, or produce false positives from matching text inside string literals/comments.
    /// However, this limitation is acceptable because (1) Roslyn compilation will fail if a referenced
    /// property doesn't exist or isn't accessible, and (2) policy enforcement via
    /// <see cref="TryGetDbSetEntityType"/> ensures only DbSet-rooted access is evaluated. Access via
    /// <c>DbContext.Set&lt;T&gt;()</c>, <c>Database.ExecuteSqlRaw()</c>, or reflection is outside the
    /// policy scope and remains controlled by EF Core and the hosting application's own
    /// DbContext-level restrictions.</remarks>
    internal static IReadOnlyList<string> ResolveReferencedEntityNames(Type contextType, string rootName, string expressionText)
    {
        ArgumentNullException.ThrowIfNull(contextType);
        ArgumentNullException.ThrowIfNull(rootName);
        ArgumentNullException.ThrowIfNull(expressionText);

        var names = new List<string>();
        var rootProperty = contextType.GetProperty(rootName, BindingFlags.Instance | BindingFlags.Public);
        var rootEntityType = TryGetDbSetEntityType(rootProperty);
        names.Add(rootEntityType?.Name ?? rootName);

        foreach (var property in contextType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.Name == rootName) continue;
            var entityType = TryGetDbSetEntityType(property);
            if (entityType is null) continue;
            if (!System.Text.RegularExpressions.Regex.IsMatch(expressionText, $@"\b{System.Text.RegularExpressions.Regex.Escape(property.Name)}\b")) continue;
            names.Add(entityType.Name);
        }
        return names;
    }

    private static Type? TryGetDbSetEntityType(PropertyInfo? property)
    {
        if (property is null) return null;
        if (!property.PropertyType.IsGenericType || property.PropertyType.GetGenericTypeDefinition() != typeof(DbSet<>)) return null;
        return property.PropertyType.GetGenericArguments()[0];
    }

    /// <summary>Finds the smallest <c>Take</c> already present on <paramref name="expression"/> (if
    /// any) and clamps it against the configured default/max, so a compiled Roslyn query's own
    /// paging behavior is respected while still being bounded - the check walks the queryable's own
    /// expression tree, regardless of how that tree was built.</summary>
    internal static int GetEffectiveTake(Expression expression, QueryExecutionOptions options)
    {
        if (options.MaxTake <= 0 || options.DefaultTake <= 0)
            throw new InvalidOperationException("Query execution take limits must be positive.");

        var take = new TakeFinder().Find(expression);
        return Math.Min(take ?? options.DefaultTake, options.MaxTake);
    }

    /// <summary>Materializes an <see cref="IQueryable"/> whose element type is only known at
    /// runtime - a Roslyn-compiled query's result is untyped-at-compile-time from this executor's
    /// point of view.</summary>
    internal static async Task<List<object?>> MaterializeUntypedAsync(IQueryable query, CancellationToken cancellationToken)
    {
        var task = (Task)MaterializeMethod.MakeGenericMethod(query.ElementType).Invoke(null, [query, cancellationToken])!;
        await task.ConfigureAwait(false);
        return (List<object?>)task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    /// <summary>Materializes <paramref name="sequence"/> up to <paramref name="effectiveTake"/> rows,
    /// reporting whether at least one further row exists (P0 #2 <c>hasMoreRows</c>). The Roslyn engine
    /// hands back a filtered, skipped <see cref="IQueryable"/> (ordering is optional and depends on caller; add OrderBy for stable pagination) at this point, so a
    /// sentinel-row approach is used to detect further rows without over-fetching.
    /// For a positive <paramref name="effectiveTake"/>, requests <c>effectiveTake + 1</c> rows and
    /// treats a returned extra row solely as a sentinel: it is discarded before projection and never
    /// counted in <see cref="QueryResult.RowCount"/> or included in <see cref="QueryResult.Rows"/>.
    /// For <paramref name="effectiveTake"/> of zero, no sentinel probe is issued at all - the sequence
    /// is never materialized and <c>hasMoreRows</c> is unconditionally <c>false</c>. Consecutive
    /// caller-supplied <c>Take</c> calls at the outer page-pipeline level are stripped first: composing a
    /// new <c>Take(effectiveTake + 1)</c> on top of <c>Take(N)</c> would otherwise reduce to
    /// <c>Take(min(N, effectiveTake + 1))</c>, which equals <c>Take(N)</c> whenever
    /// <c>N &lt;= effectiveTake</c> and would silently prevent the sentinel row from ever being requested.
    /// Takes nested in set-operation branches are preserved because they affect that branch's result
    /// semantics.</summary>
    internal static async Task<(List<object?> Values, bool HasMoreRows)> MaterializeWithContinuationAsync(
        IQueryable sequence, int effectiveTake, CancellationToken cancellationToken)
    {
        if (effectiveTake == 0) return ([], false);

        var withoutExistingTake = TakeRemover.RemovePagePipelineTakes(sequence.Expression);
        var page = sequence.Provider.CreateQuery(Expression.Call(
            typeof(Queryable), nameof(Queryable.Take), [sequence.ElementType], withoutExistingTake,
            Expression.Constant(effectiveTake + 1)));
        var values = await MaterializeUntypedAsync(page, cancellationToken).ConfigureAwait(false);
        var hasMoreRows = values.Count > effectiveTake;
        if (hasMoreRows) values.RemoveAt(values.Count - 1);
        return (values, hasMoreRows);
    }

    private static async Task<List<object?>> MaterializeAsync<T>(IQueryable query, CancellationToken cancellationToken)
    {
        var values = await ((IQueryable<T>)query).ToListAsync(cancellationToken).ConfigureAwait(false);
        return values.Cast<object?>().ToList();
    }

    /// <summary>Projects a single materialized row/scalar into the row-shape dictionary contract
    /// returned by <see cref="QueryResult"/>.</summary>
    internal static IReadOnlyDictionary<string, object?> ProjectValue(object? value)
    {
        if (value is null || IsScalar(value.GetType())) return new Dictionary<string, object?> { ["Value"] = value };
        if (value is IDictionary dictionary)
            return dictionary.Keys.Cast<object>().ToDictionary(key => key.ToString()!, key => dictionary[key]);
        return value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .ToDictionary(property => property.Name, property => property.GetValue(value));
    }

    internal static bool IsScalar(Type type) => type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(Guid) || Nullable.GetUnderlyingType(type) is not null;

    private sealed class TakeFinder : ExpressionVisitor
    {
        private int? _take;

        public int? Find(Expression expression)
        {
            Visit(expression);
            return _take;
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(Queryable) && node.Method.Name == nameof(Queryable.Take)
                && node.Arguments.Count == 2 && node.Arguments[1] is ConstantExpression { Value: int take })
                _take = _take is null ? take : Math.Min(_take.Value, take);
            return base.VisitMethodCall(node);
        }
    }

    /// <summary>Removes only consecutive outer <c>Queryable.Take</c> calls from a final page pipeline.
    /// Takes nested in a set-operation branch are part of that branch's requested result semantics and
    /// must remain intact.</summary>
    private static class TakeRemover
    {
        public static Expression RemovePagePipelineTakes(Expression expression)
        {
            while (expression is MethodCallExpression methodCall &&
                   methodCall.Method.DeclaringType == typeof(Queryable) &&
                   methodCall.Method.Name == nameof(Queryable.Take) &&
                   methodCall.Arguments.Count == 2 &&
                   methodCall.Arguments[1] is ConstantExpression { Value: int })
            {
                expression = methodCall.Arguments[0];
            }

            return expression;
        }
    }

}