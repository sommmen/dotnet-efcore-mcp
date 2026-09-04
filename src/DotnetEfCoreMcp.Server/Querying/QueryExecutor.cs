using System.Collections;
using System.Diagnostics;
using System.Linq.Dynamic.Core;
using System.Linq.Dynamic.Core.Exceptions;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>Executes a strictly allowlisted LINQPad-style expression against a public DbSet.</summary>
public sealed class QueryExecutor
{
    private static readonly MethodInfo AsNoTrackingMethod = typeof(EntityFrameworkQueryableExtensions).GetMethods()
        .Single(method => method.Name == nameof(EntityFrameworkQueryableExtensions.AsNoTracking) && method.GetParameters().Length == 1);
    private static readonly MethodInfo OrderByMethod = typeof(Queryable).GetMethods()
        .Single(method => method.Name == nameof(Queryable.OrderBy) && method.GetParameters().Length == 2);
    private static readonly MethodInfo MaterializeMethod = typeof(QueryExecutor).GetMethod(nameof(MaterializeAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly QueryExecutionOptions _options;
    private readonly ILogger<QueryExecutor> _logger;

    public QueryExecutor(QueryExecutionOptions options, ILogger<QueryExecutor> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<QueryResult> ExecuteAsync(DbContext context, QueryRequest request, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await ExecuteAsyncCore(context, request, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Query executed. Context={ContextType} Root={Root} RowCount={RowCount} DurationMs={DurationMs}", context.GetType().Name, result.Entity, result.RowCount, stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Query failed. Context={ContextType} DurationMs={DurationMs}", context.GetType().Name, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    private async Task<QueryResult> ExecuteAsyncCore(DbContext context, QueryRequest request, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        var (rootName, expressionText) = NormalizeAndGetRoot(request.Query, _options.MaxQueryLength);
        var dbSetProperty = context.GetType().GetProperty(rootName, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new QueryExecutionException($"'{rootName}' is not a public DbSet property on context '{context.GetType().Name}'.");
        if (!dbSetProperty.PropertyType.IsGenericType || dbSetProperty.PropertyType.GetGenericTypeDefinition() != typeof(DbSet<>))
            throw new QueryExecutionException($"'{rootName}' is not a public DbSet property on context '{context.GetType().Name}'.");

        var entityType = dbSetProperty.PropertyType.GetGenericArguments()[0];
        if (context.Model.FindEntityType(entityType) is null)
            throw new QueryExecutionException($"DbSet '{rootName}' is not part of this context's model.");

        // Other public DbSets on the context are registered as additional named lambda parameters so that
        // set operators can reference a second DbSet by its property name
        // (e.g. `Customers.Select(c => c.Name).Union(Orders.Select(o => o.OwnerName))`).
        // Only DbSets actually mentioned in the query text are registered: contexts can expose hundreds of
        // DbSets, and registering all of them as extra lambda parameters can force Dynamic LINQ's parser to
        // emit a custom delegate type (once the parameter count exceeds the built-in Func<> arities) via
        // Reflection.Emit into a non-collectible dynamic assembly, which cannot reference entity types loaded
        // into this context's collectible AssemblyLoadContext (NotSupportedException). Filtering keeps the
        // parameter count minimal and, incidentally, avoids that failure for the common case.
        var otherDbSets = GetOtherDbSetProperties(context, rootName)
            .Where(candidate => System.Text.RegularExpressions.Regex.IsMatch(expressionText, $@"\b{System.Text.RegularExpressions.Regex.Escape(candidate.Property.Name)}\b"))
            .ToList();
        var lambda = ParseExpression(rootName, entityType, otherDbSets, expressionText);
        new QueryExpressionValidator(_options).Validate(lambda.Body);

        var source = (IQueryable)dbSetProperty.GetValue(context)!;
        source = ApplyNoTrackingAndKeyOrder(source, entityType, context);
        var substitutions = new List<(Expression From, Expression To)> { (lambda.Parameters[0], Expression.Constant(source, typeof(IQueryable<>).MakeGenericType(entityType))) };
        for (var i = 0; i < otherDbSets.Count; i++)
        {
            var (otherProperty, otherEntityType) = otherDbSets[i];
            var otherSource = (IQueryable)otherProperty.GetValue(context)!;
            otherSource = ApplyNoTrackingAndKeyOrder(otherSource, otherEntityType, context);
            substitutions.Add((lambda.Parameters[i + 1], Expression.Constant(otherSource, typeof(IQueryable<>).MakeGenericType(otherEntityType))));
        }
        var body = new ReplaceExpressionVisitor(substitutions).Visit(lambda.Body)!;

        object execution;
        try
        {
            execution = Expression.Lambda<Func<object>>(Expression.Convert(body, typeof(object))).Compile().Invoke();
        }
        catch (Exception ex) when (ex is ParseException or InvalidOperationException or ArgumentException)
        {
            throw new QueryExecutionException("The query expression is invalid or uses an unsupported operation.", ex);
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(commandTimeoutSeconds) + _options.CancellationMargin);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            if (execution is IQueryable sequence)
            {
                var effectiveTake = GetEffectiveTake(lambda.Body, _options);
                var (values, hasMoreRows) = await MaterializeWithContinuationAsync(sequence, effectiveTake, linkedCts.Token).ConfigureAwait(false);
                return new QueryResult(rootName, values.Count, effectiveTake, hasMoreRows, false, null, values.Select(ProjectValue).ToList());
            }

            return new QueryResult(rootName, 1, null, false, true, execution, []);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            throw new QueryExecutionException($"Query against DbSet '{rootName}' timed out after {commandTimeoutSeconds}s.", ex);
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException)
        {
            throw new QueryExecutionException("The query could not be translated or executed by the database provider.", ex);
        }
    }

    private static (string Root, string Expression) NormalizeAndGetRoot(string? query, int maxQueryLength)
    {
        var value = query?.Trim();
        if (maxQueryLength <= 0) throw new InvalidOperationException("Query execution option MaxQueryLength must be positive.");
        if (value is { Length: > 0 } && value.Length > maxQueryLength)
            throw new QueryExecutionException("`query` exceeds the configured maximum length.");
        if (string.IsNullOrWhiteSpace(value)) throw new QueryExecutionException("`query` must be a non-empty LINQ expression.");
        if (value.EndsWith(';')) value = value[..^1].TrimEnd();
        if (value.Contains(';')) throw new QueryExecutionException("`query` must contain one expression only.");
        var match = System.Text.RegularExpressions.Regex.Match(value, "^(?<root>[A-Za-z_][A-Za-z0-9_]*)");
        if (!match.Success) throw new QueryExecutionException("`query` must start with a DbSet property name.");
        return (match.Groups["root"].Value, value);
    }

    /// <summary>
    /// Returns every other public, model-mapped <see cref="DbSet{TEntity}"/> property on <paramref name="context"/> so it can be
    /// registered as an additional named parameter, enabling cross-DbSet set operators such as Concat/Union/Except/Intersect
    /// (e.g. <c>Customers.Select(c => c.Name).Union(Orders.Select(o => o.OwnerName))</c>).
    /// </summary>
    private static IReadOnlyList<(PropertyInfo Property, Type EntityType)> GetOtherDbSetProperties(DbContext context, string rootName)
    {
        var result = new List<(PropertyInfo, Type)>();
        foreach (var property in context.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.Name == rootName) continue;
            if (!property.PropertyType.IsGenericType || property.PropertyType.GetGenericTypeDefinition() != typeof(DbSet<>)) continue;
            var entityType = property.PropertyType.GetGenericArguments()[0];
            if (context.Model.FindEntityType(entityType) is null) continue;
            result.Add((property, entityType));
        }
        return result;
    }

    private static LambdaExpression ParseExpression(string rootName, Type entityType, IReadOnlyList<(PropertyInfo Property, Type EntityType)> otherDbSets, string expression)
    {
        var parameters = new ParameterExpression[otherDbSets.Count + 1];
        parameters[0] = Expression.Parameter(typeof(IQueryable<>).MakeGenericType(entityType), rootName);
        for (var i = 0; i < otherDbSets.Count; i++)
            parameters[i + 1] = Expression.Parameter(typeof(IQueryable<>).MakeGenericType(otherDbSets[i].EntityType), otherDbSets[i].Property.Name);
        try
        {
            return DynamicExpressionParser.ParseLambda(new ParsingConfig { ResolveTypesBySimpleName = false }, false, parameters, typeof(object), expression);
        }
        catch (Exception ex) when (ex is ParseException or InvalidOperationException or ArgumentException)
        {
            // Dynamic LINQ Core's string parser cannot represent operators that need a dual-parameter lambda scope
            // (e.g. Join/GroupJoin/SelectMany/Zip); it throws ParseException/InvalidOperationException/ArgumentException
            // depending on the operator. Use navigation-property predicates (e.g. `Orders.Where(o => o.Customer.Name == ...)`)
            // or Concat/Union/Except/Intersect for cross-DbSet set operations instead.
            throw new QueryExecutionException(
                "`query` is not a valid supported LINQ expression. If this uses Join/GroupJoin/SelectMany/Zip, note that these " +
                "are unsupported by the dynamic LINQ parser; use a navigation-property predicate (e.g. `Orders.Where(o => o.Customer.Name == \"Alice\")`) " +
                "or Concat/Union/Except/Intersect for cross-DbSet operations instead.", ex);
        }
    }

    /// <summary>Finds the smallest <c>Take</c> already present on <paramref name="expression"/> (if
    /// any) and clamps it against the configured default/max, so callers of any query engine that
    /// produces an <see cref="IQueryable"/> (Dynamic LINQ today, Roslyn-compiled real C# tomorrow)
    /// get identical paging behavior - the check walks the queryable's own expression tree, which
    /// exists regardless of how that tree was built.</summary>
    internal static int GetEffectiveTake(Expression expression, QueryExecutionOptions options)
    {
        if (options.MaxTake <= 0 || options.DefaultTake <= 0)
            throw new InvalidOperationException("Query execution take limits must be positive.");

        var take = new TakeFinder().Find(expression);
        return Math.Min(take ?? options.DefaultTake, options.MaxTake);
    }

    /// <summary>Applies <c>AsNoTracking</c> and a deterministic primary-key ordering to
    /// <paramref name="source"/>. Shared across query engines: any engine handing back a raw
    /// <see cref="IQueryable"/> for a DbSet root needs the same read-only, stable-paging
    /// semantics.</summary>
    internal static IQueryable ApplyNoTrackingAndKeyOrder(IQueryable source, Type entityType, DbContext context)
    {
        var noTracking = (IQueryable)AsNoTrackingMethod.MakeGenericMethod(entityType).Invoke(null, [source])!;
        var key = context.Model.FindEntityType(entityType)!.FindPrimaryKey()?.Properties.FirstOrDefault()?.PropertyInfo;
        if (key is null) return noTracking;
        var parameter = Expression.Parameter(entityType, "e");
        var selector = Expression.Lambda(Expression.Property(parameter, key), parameter);
        return (IQueryable)OrderByMethod.MakeGenericMethod(entityType, key.PropertyType).Invoke(null, [noTracking, selector])!;
    }

    /// <summary>Materializes an <see cref="IQueryable"/> whose element type is only known at
    /// runtime (shared across query engines - a Roslyn-compiled query's result is just as
    /// untyped-at-compile-time from this executor's point of view as a Dynamic LINQ one).</summary>
    internal static async Task<List<object?>> MaterializeUntypedAsync(IQueryable query, CancellationToken cancellationToken)
    {
        var task = (Task)MaterializeMethod.MakeGenericMethod(query.ElementType).Invoke(null, [query, cancellationToken])!;
        await task.ConfigureAwait(false);
        return (List<object?>)task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    /// <summary>Materializes <paramref name="sequence"/> up to <paramref name="effectiveTake"/> rows,
    /// reporting whether at least one further row exists (P0 #2 <c>hasMoreRows</c>). Shared across
    /// query engines: both the Dynamic LINQ and Roslyn engines hand back a filtered, ordered, skipped
    /// <see cref="IQueryable"/> at this point, so the same sentinel-row approach applies to either.
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
    /// returned by <see cref="QueryResult"/>. Shared across query engines: the output contract does
    /// not depend on how the value was produced.</summary>
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

    private sealed class ReplaceExpressionVisitor(IReadOnlyList<(Expression From, Expression To)> substitutions) : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node)
        {
            foreach (var (from, to) in substitutions)
                if (node == from) return to;
            return base.Visit(node);
        }
    }

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

    private sealed class QueryExpressionValidator(QueryExecutionOptions options) : ExpressionVisitor
    {
        // Note: Join, GroupJoin, SelectMany and Zip are intentionally excluded. Dynamic LINQ Core's string parser
        // resolves every instance call through Expression.Call against the standard Queryable/Enumerable generic
        // method definitions; it cannot express these operators' multi-parameter-scope or delegate-shape
        // requirements from a parsed string in any syntax form, so they always fail with an opaque
        // "No generic method '<name>' ... is compatible" InvalidOperationException. Use a navigation-property
        // predicate instead (e.g. `Orders.Where(o => o.Customer.Name == "Alice")`), or Concat/Union/Except/Intersect
        // for combining two DbSets, which do work through the parser.
        private static readonly HashSet<string> QueryOperators = ["Where", "Select", "GroupBy", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending", "Skip", "Take", "Distinct", "Count", "LongCount", "Sum", "Average", "Min", "Max", "First", "FirstOrDefault", "Single", "SingleOrDefault", "Any", "All", "Concat", "Union", "Except", "Intersect"];
        private static readonly HashSet<string> StringMethods = ["Contains", "StartsWith", "EndsWith", "ToLower", "ToUpper", "Trim"];
        private int _nodes;
        private int _operatorCalls;
        private int _depth;

        public void Validate(Expression expression) => Visit(expression);

        public override Expression? Visit(Expression? node)
        {
            if (node is null) return null;
            if (++_nodes > options.MaxExpressionNodes)
                throw new QueryExecutionException("`query` exceeds the configured expression complexity limit.");
            if (++_depth > options.MaxExpressionDepth)
                throw new QueryExecutionException("`query` exceeds the configured expression nesting limit.");
            try { return base.Visit(node); }
            finally { _depth--; }
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var declaring = node.Method.DeclaringType;
            if (declaring == typeof(Queryable) && QueryOperators.Contains(node.Method.Name))
            {
                if (++_operatorCalls > options.MaxQueryOperators)
                    throw new QueryExecutionException("`query` exceeds the configured operator limit.");
                return base.VisitMethodCall(node);
            }
            if (declaring == typeof(string) && StringMethods.Contains(node.Method.Name)) return base.VisitMethodCall(node);
            throw new QueryExecutionException($"Method '{node.Method.Name}' is not permitted in `query`.");
        }
        protected override Expression VisitNew(NewExpression node) => node.Members is null ? throw new QueryExecutionException("Object construction is not permitted in `query`.") : base.VisitNew(node);
        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Member is not PropertyInfo) throw new QueryExecutionException("Only property access is permitted in `query`.");
            return base.VisitMember(node);
        }
        protected override Expression VisitInvocation(InvocationExpression node) => throw new QueryExecutionException("Invocations are not permitted in `query`.");
        protected override Expression VisitBlock(BlockExpression node) => throw new QueryExecutionException("Statements are not permitted in `query`.");
    }
}