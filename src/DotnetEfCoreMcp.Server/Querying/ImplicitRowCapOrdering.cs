using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>Appends a deterministic primary-key ordering to a query that the server is about to cap
/// with its own implicit <c>Take</c>.
///
/// <para>The cap is a server-added safety measure, not user intent, so it must not change what the
/// query means or whether it runs at all. Without an ordering it does both: the capped page is an
/// arbitrary subset, and against a target <see cref="DbContext"/> that escalates EF Core's
/// <c>RowLimitingOperationWithoutOrderByWarning</c> to an error (a common <c>#if DEBUG</c> practice)
/// the query fails before reaching the database - even though the same query succeeds uncapped
/// (issue #97).</para>
///
/// <para>Ordering is best-effort and deliberately silent when it cannot be applied: a query already
/// carrying its own ordering keeps it untouched, a projection is ordered by the mapped source
/// underneath it, a key-less sequence of scalars is ordered by the scalar value, and anything else
/// (anonymous types, groupings, set operations) is left exactly as it was rather than being
/// rejected - the cap must never turn an otherwise-valid query into a failure.</para></summary>
internal static class ImplicitRowCapOrdering
{
    public static Expression Apply(Expression expression, IModel model)
    {
        if (ContainsOrdering(expression))
            return expression;

        if (OrderByPrimaryKey(expression, model) is { } orderedEntities)
            return orderedEntities;

        // A projection erases the mapped entity type the key lives on (`Customers.Select(c => c.Name)`
        // is an IQueryable<string>), so trailing projections are peeled off, the ordering is applied to
        // the mapped source underneath, and the projections are rebuilt on top of it.
        var projections = new Stack<MethodCallExpression>();
        var source = expression;
        Expression? ordered = null;
        while (source is MethodCallExpression { Method.DeclaringType: var declaring, Arguments.Count: 2 } call
               && declaring == typeof(Queryable)
               && call.Method.Name == nameof(Queryable.Select))
        {
            projections.Push(call);
            source = call.Arguments[0];
            if (OrderByPrimaryKey(source, model) is { } orderedSource)
            {
                ordered = orderedSource;
                break;
            }
        }

        if (ordered is null)
        {
            // Nothing mapped left to order by (an aggregate, a set operation, a Distinct that erased
            // the key). A scalar element orders deterministically by its own value; anything else is
            // left alone rather than risking an untranslatable ordering.
            return OrderByScalarElement(expression) ?? expression;
        }

        while (projections.Count > 0)
        {
            var projection = projections.Pop();
            ordered = Expression.Call(projection.Method, ordered, projection.Arguments[1]);
        }

        return ordered;
    }

    /// <summary>Orders a sequence of scalars by the scalar value itself - the only ordering available
    /// once a projection or set operator has erased every mapped entity the query was rooted at.</summary>
    private static Expression? OrderByScalarElement(Expression expression)
    {
        var elementType = GetQueryableElementType(expression);
        if (elementType is null || !IsOrderableScalar(elementType))
            return null;

        var parameter = Expression.Parameter(elementType, "value");
        var selector = Expression.Lambda(parameter, parameter);
        return Expression.Call(typeof(Queryable), nameof(Queryable.OrderBy), [elementType, elementType],
            expression, Expression.Quote(selector));
    }

    private static bool IsOrderableScalar(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsPrimitive
            || underlying.IsEnum
            || underlying == typeof(string)
            || underlying == typeof(decimal)
            || underlying == typeof(DateTime)
            || underlying == typeof(DateTimeOffset)
            || underlying == typeof(DateOnly)
            || underlying == typeof(TimeOnly)
            || underlying == typeof(TimeSpan)
            || underlying == typeof(Guid);
    }

    /// <summary>Orders <paramref name="expression"/> by every CLR-backed primary-key property of its
    /// element type, or returns <c>null</c> when that element type is not a mapped entity with such a
    /// key - the implicit cap must never turn an otherwise-valid query into a failure.</summary>
    private static Expression? OrderByPrimaryKey(Expression expression, IModel model)
    {
        var elementType = GetQueryableElementType(expression);
        if (elementType is null)
            return null;

        var key = model.FindEntityType(elementType)?.FindPrimaryKey();
        if (key is null)
            return null;

        Expression ordered = expression;
        var applied = 0;
        foreach (var property in key.Properties)
        {
            if (property.PropertyInfo is null)
                continue;

            var parameter = Expression.Parameter(elementType, "entity");
            var selector = Expression.Lambda(Expression.Property(parameter, property.PropertyInfo), parameter);
            var method = applied == 0 ? nameof(Queryable.OrderBy) : nameof(Queryable.ThenBy);
            ordered = Expression.Call(typeof(Queryable), method, [elementType, selector.ReturnType],
                ordered, Expression.Quote(selector));
            applied++;
        }

        return applied == 0 ? null : ordered;
    }

    /// <summary>Reports whether the query's own outer pipeline already orders its rows. Orderings
    /// nested inside a subquery (a <c>Where</c> predicate, a <c>Select</c> projection, a set-operation
    /// branch) do not order the outer result, so they are not treated as one.</summary>
    private static bool ContainsOrdering(Expression expression)
    {
        while (expression is MethodCallExpression call && call.Method.DeclaringType == typeof(Queryable))
        {
            if (call.Method.Name is nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending)
                or nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending))
            {
                return true;
            }

            if (call.Arguments.Count == 0)
                break;

            expression = call.Arguments[0];
        }

        return false;
    }

    private static Type? GetQueryableElementType(Expression expression) =>
        expression.Type.IsGenericType && expression.Type.GetGenericTypeDefinition() == typeof(IQueryable<>)
            ? expression.Type.GetGenericArguments()[0]
            : null;
}
