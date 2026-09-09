using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>Builds and validates forward-only keyset pages for mapped entity queries.</summary>
internal static class CursorPaginationExecutor
{
    internal const string InvalidCursorMessage = "The cursor pagination request is invalid.";

    public static async Task<QueryResult> ExecuteAsync(
        IQueryable sequence,
        DbContext context,
        Type contextType,
        QueryPagination pagination,
        QueryExecutionOptions options,
        int effectiveTake,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(pagination.Mode, "cursor", StringComparison.OrdinalIgnoreCase))
            throw new QueryExecutionException("`pagination.mode` must be `cursor`.");
        if (ContainsSkip(sequence.Expression))
            throw new QueryExecutionException("Cursor pagination cannot be combined with LINQ Skip().");
        if (sequence.ElementType != context.Model.FindEntityType(sequence.ElementType)?.ClrType)
            throw new QueryExecutionException("Cursor pagination requires a query returning a mapped entity type.");

        try
        {
            var entityType = context.Model.FindEntityType(sequence.ElementType)
                ?? throw new QueryExecutionException("Cursor pagination requires a query returning a mapped entity type.");
            var orderings = GetOrderings(sequence.Expression);
            if (orderings.Count == 0)
                throw new QueryExecutionException("Cursor pagination requires an explicit deterministic OrderBy().");

            ValidateOrderingTypesAreSupported(orderings);
            AppendMissingPrimaryKeyOrderings(orderings, entityType);
            var orderingShape = orderings.Select(OrderingShape.From).ToArray();
            var baseExpression = RemoveTrailingTakes(sequence.Expression);
            var ordered = ApplyOrdering(sequence.Provider, baseExpression, sequence.ElementType, orderings);

            if (pagination.Cursor is not null)
            {
                var cursor = DecodeCursor(pagination.Cursor, options.CursorSigningKey);
                if (!Matches(cursor, contextType, entityType, orderingShape))
                    throw new QueryExecutionException(InvalidCursorMessage);
                ordered = ApplySeek(sequence.Provider, ordered, sequence.ElementType, orderings, cursor.Values);
            }

            var (values, hasMoreRows) = await QueryExecutor.MaterializeWithContinuationAsync(
                ordered, effectiveTake, cancellationToken).ConfigureAwait(false);
            var nextCursor = hasMoreRows && values.Count > 0
                ? EncodeCursor(contextType, entityType, orderingShape, ReadValues(orderings, values[^1]!), options.CursorSigningKey)
                : null;
            return new QueryResult("C#", values.Count, effectiveTake, hasMoreRows, false, null,
                values.Select(QueryExecutor.ProjectValue).ToList(), nextCursor);
        }
        catch (QueryExecutionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException or FormatException or CryptographicException)
        {
            throw new QueryExecutionException(InvalidCursorMessage, ex);
        }
    }

    private static List<Ordering> GetOrderings(Expression expression)
    {
        var result = new List<Ordering>();
        CollectOrderings(expression, result);
        return result;
    }

    private static void CollectOrderings(Expression expression, List<Ordering> result)
    {
        if (expression is not MethodCallExpression { Method.DeclaringType: { } declaringType } call ||
            declaringType != typeof(Queryable) || call.Arguments.Count == 0)
            return;

        CollectOrderings(call.Arguments[0], result);
        if (call.Method.Name is not (nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending) or
            nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending)) || call.Arguments.Count != 2)
            return;

        var selector = Unquote(call.Arguments[1]) as LambdaExpression
            ?? throw new QueryExecutionException("Cursor pagination requires an explicit deterministic OrderBy().");
        var isPrimaryOrder = call.Method.Name is nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending);
        if (isPrimaryOrder)
            result.Clear();
        result.Add(new Ordering(selector, call.Method.Name.EndsWith("Descending", StringComparison.Ordinal)));
    }

    private static void AppendMissingPrimaryKeyOrderings(List<Ordering> orderings, IEntityType entityType)
    {
        var key = entityType.FindPrimaryKey()
            ?? throw new QueryExecutionException("Cursor pagination requires an entity with a primary key.");
        foreach (var property in key.Properties)
        {
            if (property.PropertyInfo is null)
                throw new QueryExecutionException("Cursor pagination requires CLR primary-key properties.");
            if (orderings.Any(ordering => IsPropertySelector(ordering.Selector, property.PropertyInfo)))
                continue;

            var parameter = Expression.Parameter(entityType.ClrType, "entity");
            orderings.Add(new Ordering(Expression.Lambda(Expression.Property(parameter, property.PropertyInfo), parameter), false));
        }
    }

    private static IQueryable ApplyOrdering(IQueryProvider provider, Expression source, Type elementType, IReadOnlyList<Ordering> orderings)
    {
        Expression query = source;
        for (var index = 0; index < orderings.Count; index++)
        {
            var ordering = orderings[index];
            var name = index == 0
                ? ordering.Descending ? nameof(Queryable.OrderByDescending) : nameof(Queryable.OrderBy)
                : ordering.Descending ? nameof(Queryable.ThenByDescending) : nameof(Queryable.ThenBy);
            query = Expression.Call(typeof(Queryable), name, [elementType, ordering.Selector.ReturnType],
                query, Expression.Quote(ordering.Selector));
        }

        return provider.CreateQuery(query);
    }

    private static IQueryable ApplySeek(IQueryProvider provider, IQueryable sequence, Type elementType,
        IReadOnlyList<Ordering> orderings, JsonElement[] values)
    {
        if (values.Length != orderings.Count)
            throw new QueryExecutionException(InvalidCursorMessage);

        var parameter = Expression.Parameter(elementType, "entity");
        var predicate = BuildSeekPredicate(orderings, values, parameter);
        var lambda = Expression.Lambda(predicate, parameter);
        var where = Expression.Call(typeof(Queryable), nameof(Queryable.Where), [elementType],
            sequence.Expression, Expression.Quote(lambda));
        return provider.CreateQuery(where);
    }

    private static Expression BuildSeekPredicate(IReadOnlyList<Ordering> orderings, JsonElement[] values, ParameterExpression parameter)
    {
        Expression? predicate = null;
        Expression? equalPrefix = null;
        for (var index = 0; index < orderings.Count; index++)
        {
            var ordering = orderings[index];
            var value = JsonSerializer.Deserialize(values[index].GetRawText(), ordering.Selector.ReturnType);
            var selector = ReplaceParameter(ordering.Selector, parameter).Body;
            var constant = Expression.Constant(value, ordering.Selector.ReturnType);
            var comparison = BuildComparison(selector, constant, ordering.Descending);
            var term = equalPrefix is null ? comparison : Expression.AndAlso(equalPrefix, comparison);
            predicate = predicate is null ? term : Expression.OrElse(predicate, term);
            var equal = Expression.Equal(selector, constant);
            equalPrefix = equalPrefix is null ? equal : Expression.AndAlso(equalPrefix, equal);
        }

        return predicate!;
    }

    private static void ValidateOrderingTypesAreSupported(IReadOnlyList<Ordering> orderings)
    {
        foreach (var ordering in orderings)
        {
            var selectorType = ordering.Selector.ReturnType;
            var underlyingType = Nullable.GetUnderlyingType(selectorType) ?? selectorType;

            // Reject types that do not have SQL-translatable comparison operators.
            // bool, IntPtr, and UIntPtr do not have relational operators and cannot be translated to SQL in WHERE predicates.
            if (underlyingType == typeof(bool) || underlyingType == typeof(IntPtr) || underlyingType == typeof(UIntPtr))
                throw new QueryExecutionException($"Cursor pagination does not support ordering by type '{underlyingType.Name}'. " +
                    $"Only types with SQL-translatable comparison operators are supported.");
        }
    }

    private static bool HasComparisonOperators(Type type)
    {
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;

        // bool, IntPtr, and UIntPtr are primitive but do not have relational comparison operators.
        // Exclude them explicitly so they fall through to the IComparable.CompareTo path.
        if (underlyingType == typeof(bool) || underlyingType == typeof(IntPtr) || underlyingType == typeof(UIntPtr))
            return false;

        // Primitive numeric types (excluding bool/IntPtr/UIntPtr), DateTime, DateTimeOffset, TimeSpan, char have comparison operators
        if (underlyingType.IsPrimitive || underlyingType == typeof(decimal) || underlyingType == typeof(DateTime) ||
            underlyingType == typeof(DateTimeOffset) || underlyingType == typeof(TimeSpan) || underlyingType == typeof(char))
            return true;

        // Check if type defines op_GreaterThan operator
        return underlyingType.GetMethod("op_GreaterThan", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public, null, new[] { underlyingType, underlyingType }, null) is not null;
    }

    private static Expression BuildComparison(Expression left, Expression right, bool descending)
    {
        if (left.Type == typeof(string))
        {
            var compare = Expression.Call(typeof(string), nameof(string.Compare), Type.EmptyTypes, left, right);
            return descending ? Expression.LessThan(compare, Expression.Constant(0)) : Expression.GreaterThan(compare, Expression.Constant(0));
        }

        if (HasComparisonOperators(left.Type))
            return descending ? Expression.LessThan(left, right) : Expression.GreaterThan(left, right);

        var compareTo = Expression.Call(
            Expression.Convert(left, typeof(IComparable)),
            nameof(IComparable.CompareTo),
            Type.EmptyTypes,
            Expression.Convert(right, typeof(object)));
        return descending ? Expression.LessThan(compareTo, Expression.Constant(0)) : Expression.GreaterThan(compareTo, Expression.Constant(0));
    }

    private static object?[] ReadValues(IReadOnlyList<Ordering> orderings, object value)
    {
        try
        {
            return orderings.Select(ordering => ordering.Selector.Compile().DynamicInvoke(value)).ToArray();
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // DynamicInvoke wraps exceptions; unwrap and convert to QueryExecutionException for consistent error handling
            throw new QueryExecutionException(InvalidCursorMessage, ex.InnerException);
        }
    }

    private static string EncodeCursor(Type contextType, IEntityType entityType, OrderingShape[] ordering, object?[] values, string signingKey)
    {
        var payload = new CursorPayload
        {
            Context = contextType.FullName ?? contextType.Name,
            Entity = entityType.Name,
            Ordering = ordering,
            Values = values.Select(value => JsonSerializer.SerializeToElement(value)).ToArray(),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        return $"{Base64UrlEncode(bytes)}.{Base64UrlEncode(Sign(bytes, signingKey))}";
    }

    private static CursorPayload DecodeCursor(string token, string signingKey)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 2)
                throw new FormatException();
            var payload = Base64UrlDecode(parts[0]);
            var signature = Base64UrlDecode(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(signature, Sign(payload, signingKey)))
                throw new CryptographicException();
            return JsonSerializer.Deserialize<CursorPayload>(payload)
                ?? throw new JsonException();
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException or ArgumentException)
        {
            throw new QueryExecutionException(InvalidCursorMessage, ex);
        }
    }

    private static bool Matches(CursorPayload cursor, Type contextType, IEntityType entityType, OrderingShape[] ordering) =>
        cursor.Context == (contextType.FullName ?? contextType.Name) &&
        cursor.Entity == entityType.Name &&
        cursor.Ordering is not null &&
        cursor.Ordering.SequenceEqual(ordering);

    private static byte[] Sign(byte[] payload, string signingKey) =>
        !string.IsNullOrWhiteSpace(signingKey)
            ? HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), payload)
            : throw new QueryExecutionException("Cursor pagination requires a non-empty QueryExecution:CursorSigningKey.");

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => throw new FormatException() };
        return Convert.FromBase64String(padded);
    }

    private static Expression RemoveTrailingTakes(Expression expression)
    {
        while (expression is MethodCallExpression methodCall &&
               methodCall.Method.DeclaringType == typeof(Queryable) &&
               methodCall.Method.Name == nameof(Queryable.Take) &&
               methodCall.Arguments.Count == 2)
        {
            expression = methodCall.Arguments[0];
        }

        return expression;
    }

    private static bool ContainsSkip(Expression expression)
    {
        var finder = new SkipFinder();
        finder.Visit(expression);
        return finder.Found;
    }

    private static bool IsPropertySelector(LambdaExpression selector, System.Reflection.PropertyInfo property)
    {
        var body = selector.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : selector.Body;
        return body is MemberExpression { Member: System.Reflection.PropertyInfo selected } && selected == property;
    }

    private static LambdaExpression ReplaceParameter(LambdaExpression lambda, ParameterExpression parameter) =>
        Expression.Lambda(new ParameterReplacer(lambda.Parameters[0], parameter).Visit(lambda.Body)!, parameter);

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }

    private sealed class SkipFinder : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            Found |= node.Method.DeclaringType == typeof(Queryable) && node.Method.Name == nameof(Queryable.Skip);
            return base.VisitMethodCall(node);
        }
    }

    private static Expression Unquote(Expression expression) =>
        expression is UnaryExpression { NodeType: ExpressionType.Quote } quote ? quote.Operand : expression;

    private sealed record Ordering(LambdaExpression Selector, bool Descending);
    private sealed record OrderingShape(string Expression, bool Descending, string Type)
    {
        public static OrderingShape From(Ordering ordering) =>
            new(ExtractPropertyPath(ordering.Selector), ordering.Descending, ordering.Selector.ReturnType.AssemblyQualifiedName!);

        /// <summary>Extracts a stable, canonical property path from a selector lambda.
        /// Uses the property name instead of Expression.ToString() to ensure cursor compatibility
        /// across process boundaries and different expression tree compilation contexts.</summary>
        private static string ExtractPropertyPath(LambdaExpression selector)
        {
            var body = selector.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert
                ? convert.Operand
                : selector.Body;

            if (body is MemberExpression { Member: PropertyInfo property })
                return property.Name;

            // Fallback for complex expressions: use a stable string representation of the expression structure
            // rather than ToString() which may vary across runs. We normalize the parameter names.
            return NormalizeExpressionPath(body, selector.Parameters[0].Name ?? "p");
        }

        /// <summary>Produces a stable normalized string representation of an expression tree,
        /// suitable for comparison across process boundaries.</summary>
        private static string NormalizeExpressionPath(Expression expr, string parameterName)
        {
            return expr switch
            {
                MemberExpression me => $"{NormalizeExpressionPath(me.Expression!, parameterName)}.{me.Member.Name}",
                ParameterExpression pe => parameterName,
                MethodCallExpression mc => $"{NormalizeExpressionPath(mc.Object!, parameterName)}.{mc.Method.Name}()",
                _ => expr.NodeType.ToString(),
            };
        }
    }

    private sealed class CursorPayload
    {
        public required string Context { get; init; }
        public required string Entity { get; init; }
        public required OrderingShape[] Ordering { get; init; }
        public required JsonElement[] Values { get; init; }
    }
}
