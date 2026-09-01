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

        var lambda = ParseExpression(entityType, expressionText);
        new QueryExpressionValidator(_options).Validate(lambda.Body);

        var source = (IQueryable)dbSetProperty.GetValue(context)!;
        source = ApplyNoTrackingAndKeyOrder(source, entityType, context);
        var body = new ReplaceExpressionVisitor(lambda.Parameters[0], Expression.Constant(source, typeof(IQueryable<>).MakeGenericType(entityType))).Visit(lambda.Body)!;

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
                var page = sequence.Take(effectiveTake);
                var values = await MaterializeUntypedAsync(page, linkedCts.Token).ConfigureAwait(false);
                return new QueryResult(rootName, values.Count, effectiveTake, false, null, values.Select(ProjectValue).ToList());
            }

            return new QueryResult(rootName, 1, null, true, execution, []);
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
        return (match.Groups["root"].Value, "it" + value[match.Groups["root"].Length..]);
    }

    private static LambdaExpression ParseExpression(Type entityType, string expression)
    {
        try
        {
            return DynamicExpressionParser.ParseLambda(new ParsingConfig { ResolveTypesBySimpleName = false }, false, [Expression.Parameter(typeof(IQueryable<>).MakeGenericType(entityType), "it")], typeof(object), expression);
        }
        catch (ParseException ex)
        {
            throw new QueryExecutionException("`query` is not a valid supported LINQ expression.", ex);
        }
    }

    private static int GetEffectiveTake(Expression expression, QueryExecutionOptions options)
    {
        if (options.MaxTake <= 0 || options.DefaultTake <= 0)
            throw new InvalidOperationException("Query execution take limits must be positive.");

        var take = new TakeFinder().Find(expression);
        return Math.Min(take ?? options.DefaultTake, options.MaxTake);
    }

    private static IQueryable ApplyNoTrackingAndKeyOrder(IQueryable source, Type entityType, DbContext context)
    {
        var noTracking = (IQueryable)AsNoTrackingMethod.MakeGenericMethod(entityType).Invoke(null, [source])!;
        var key = context.Model.FindEntityType(entityType)!.FindPrimaryKey()?.Properties.FirstOrDefault()?.PropertyInfo;
        if (key is null) return noTracking;
        var parameter = Expression.Parameter(entityType, "e");
        var selector = Expression.Lambda(Expression.Property(parameter, key), parameter);
        return (IQueryable)OrderByMethod.MakeGenericMethod(entityType, key.PropertyType).Invoke(null, [noTracking, selector])!;
    }

    private static async Task<List<object?>> MaterializeUntypedAsync(IQueryable query, CancellationToken cancellationToken)
    {
        var task = (Task)MaterializeMethod.MakeGenericMethod(query.ElementType).Invoke(null, [query, cancellationToken])!;
        await task.ConfigureAwait(false);
        return (List<object?>)task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private static async Task<List<object?>> MaterializeAsync<T>(IQueryable query, CancellationToken cancellationToken)
    {
        var values = await ((IQueryable<T>)query).ToListAsync(cancellationToken).ConfigureAwait(false);
        return values.Cast<object?>().ToList();
    }

    private static IReadOnlyDictionary<string, object?> ProjectValue(object? value)
    {
        if (value is null || IsScalar(value.GetType())) return new Dictionary<string, object?> { ["Value"] = value };
        if (value is IDictionary dictionary)
            return dictionary.Keys.Cast<object>().ToDictionary(key => key.ToString()!, key => dictionary[key]);
        return value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .ToDictionary(property => property.Name, property => property.GetValue(value));
    }

    private static bool IsScalar(Type type) => type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(Guid) || Nullable.GetUnderlyingType(type) is not null;

    private sealed class ReplaceExpressionVisitor(Expression from, Expression to) : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node) => node == from ? to : base.Visit(node);
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

    private sealed class QueryExpressionValidator(QueryExecutionOptions options) : ExpressionVisitor
    {
        private static readonly HashSet<string> QueryOperators = ["Where", "Select", "GroupBy", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending", "Skip", "Take", "Count", "LongCount", "Sum", "Average", "Min", "Max"];
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