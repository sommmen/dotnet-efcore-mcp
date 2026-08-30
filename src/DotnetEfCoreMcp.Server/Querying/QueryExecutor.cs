using System.Collections;
using System.Linq.Dynamic.Core;
using System.Linq.Dynamic.Core.Exceptions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>Executes a <see cref="QueryRequest"/> against a resolved <see cref="DbContext"/>
/// using System.Linq.Dynamic.Core, enforcing read-only, no-tracking, row-capped, allowlisted-
/// include, timeout-bounded execution.</summary>
public sealed class QueryExecutor
{
    private static readonly MethodInfo SetMethodDefinition =
        typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!;

    private static readonly MethodInfo AsNoTrackingMethodDefinition =
        typeof(EntityFrameworkQueryableExtensions).GetMethods()
            .Single(m => m.Name == nameof(EntityFrameworkQueryableExtensions.AsNoTracking)
                         && m.GetParameters().Length == 1);

    private static readonly MethodInfo IncludeStringMethodDefinition =
        typeof(EntityFrameworkQueryableExtensions).GetMethods()
            .Single(m => m.Name == nameof(EntityFrameworkQueryableExtensions.Include)
                         && m.GetParameters().Length == 2
                         && m.GetParameters()[1].ParameterType == typeof(string));

    private static readonly MethodInfo MaterializeMethodDefinition =
        typeof(QueryExecutor).GetMethod(nameof(MaterializeAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly QueryExecutionOptions _options;

    public QueryExecutor(QueryExecutionOptions options)
    {
        _options = options;
    }

    public async Task<QueryResult> ExecuteAsync(DbContext context, QueryRequest request, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        var entityType = context.Model.GetEntityTypes().FirstOrDefault(e => e.ClrType.Name == request.Entity)
            ?? throw new QueryExecutionException(
                $"Entity '{request.Entity}' is not part of this context's model. Known entities: {string.Join(", ", context.Model.GetEntityTypes().Select(e => e.ClrType.Name))}.");

        var clrType = entityType.ClrType;

        var includeNames = request.Include ?? [];
        if (includeNames.Count > 0)
        {
            var validNavigations = entityType.GetNavigations().Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
            var invalid = includeNames.Where(n => !validNavigations.Contains(n)).ToList();
            if (invalid.Count > 0)
            {
                throw new QueryExecutionException(
                    $"Invalid `include` value(s) for entity '{request.Entity}': {string.Join(", ", invalid)}. Valid navigation properties: {(validNavigations.Count > 0 ? string.Join(", ", validNavigations) : "(none)")}.");
            }
        }

        // Build the strongly (but dynamically) typed query: Set<TEntity>() -> AsNoTracking() -> Include(...) for each requested navigation.
        object dbSet;
        try
        {
            dbSet = SetMethodDefinition.MakeGenericMethod(clrType).Invoke(context, null)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new QueryExecutionException($"Could not resolve a DbSet for entity '{request.Entity}'.", ex.InnerException);
        }

        var typedQuery = AsNoTrackingMethodDefinition.MakeGenericMethod(clrType).Invoke(null, [dbSet])!;

        foreach (var navigationName in includeNames)
        {
            typedQuery = IncludeStringMethodDefinition.MakeGenericMethod(clrType).Invoke(null, [typedQuery, navigationName])!;
        }

        // From here on, everything is expressed against the non-generic IQueryable using Dynamic
        // LINQ, which accepts positional parameters (@0, @1, ...) rather than ever concatenating
        // caller-supplied values into the expression string.
        var queryable = (IQueryable)typedQuery;
        var parameters = request.Parameters?.ToArray() ?? [];

        if (!string.IsNullOrWhiteSpace(request.Where))
        {
            try
            {
                queryable = queryable.Where(request.Where, parameters);
            }
            catch (Exception ex) when (ex is ParseException)
            {
                throw new QueryExecutionException($"Invalid `where` expression: {ex.Message}", ex);
            }
        }

        if (!string.IsNullOrWhiteSpace(request.OrderBy))
        {
            try
            {
                queryable = queryable.OrderBy(request.OrderBy, parameters);
            }
            catch (Exception ex) when (ex is ParseException)
            {
                throw new QueryExecutionException($"Invalid `orderBy` expression: {ex.Message}", ex);
            }
        }

        var effectiveSkip = Math.Max(0, request.Skip ?? 0);
        var effectiveTake = Math.Clamp(request.Take ?? _options.DefaultTake, 0, _options.MaxTake);

        queryable = queryable.Skip(effectiveSkip).Take(effectiveTake);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(commandTimeoutSeconds) + _options.CancellationMargin);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        List<Dictionary<string, object?>> rows;
        try
        {
            var materializeMethod = MaterializeMethodDefinition.MakeGenericMethod(clrType);
            var task = (Task<List<Dictionary<string, object?>>>)materializeMethod.Invoke(null, [queryable, includeNames, linkedCts.Token])!;
            rows = await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            throw new QueryExecutionException($"Query against entity '{request.Entity}' timed out after {commandTimeoutSeconds}s.", ex);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException inner && timeoutCts.IsCancellationRequested)
        {
            throw new QueryExecutionException($"Query against entity '{request.Entity}' timed out after {commandTimeoutSeconds}s.", inner);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new QueryExecutionException($"Query against entity '{request.Entity}' failed: {ex.InnerException.Message}", ex.InnerException);
        }
        catch (Exception ex) when (ex is not QueryExecutionException)
        {
            throw new QueryExecutionException($"Query against entity '{request.Entity}' failed: {ex.Message}", ex);
        }

        return new QueryResult(
            Entity: request.Entity,
            RowCount: rows.Count,
            EffectiveTake: effectiveTake,
            EffectiveSkip: effectiveSkip,
            IncludedNavigations: includeNames,
            Rows: rows);
    }

    private static async Task<List<Dictionary<string, object?>>> MaterializeAsync<TEntity>(
        IQueryable queryable, IReadOnlyList<string> includeNames, CancellationToken cancellationToken)
        where TEntity : class
    {
        var typed = (IQueryable<TEntity>)queryable;
        var entities = await typed.ToListAsync(cancellationToken).ConfigureAwait(false);
        return entities.Select(e => ProjectEntity(e!, includeNames)).ToList();
    }

    /// <summary>Projects a materialized entity into a plain dictionary of scalar values, plus (for
    /// the top level only) any explicitly-requested included navigations, themselves projected to
    /// scalars-only one level deep. Capping expansion to exactly one level for included
    /// navigations - rather than following navigation properties transitively - makes the result
    /// shape bounded and free of reference cycles by construction, without needing a
    /// visited-node tracker or relying solely on JSON reference handling.</summary>
    private static Dictionary<string, object?> ProjectEntity(object entity, IReadOnlyList<string> includeNames)
    {
        var dict = new Dictionary<string, object?>();
        var type = entity.GetType();

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            if (IsScalarClrType(property.PropertyType))
            {
                dict[property.Name] = property.GetValue(entity);
                continue;
            }

            if (includeNames.Count == 0 || !includeNames.Contains(property.Name, StringComparer.Ordinal))
            {
                // Not a scalar and not an explicitly-requested navigation: skip it rather than
                // risk following an unbounded/unexpected object graph.
                continue;
            }

            var value = property.GetValue(entity);
            if (value is null)
            {
                dict[property.Name] = null;
            }
            else if (value is string)
            {
                dict[property.Name] = value;
            }
            else if (value is IEnumerable enumerable)
            {
                var items = new List<Dictionary<string, object?>>();
                foreach (var item in enumerable)
                {
                    if (item is not null)
                    {
                        items.Add(ProjectEntity(item, includeNames: []));
                    }
                }

                dict[property.Name] = items;
            }
            else
            {
                dict[property.Name] = ProjectEntity(value, includeNames: []);
            }
        }

        return dict;
    }

    private static bool IsScalarClrType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsPrimitive
               || underlying.IsEnum
               || underlying == typeof(string)
               || underlying == typeof(decimal)
               || underlying == typeof(DateTime)
               || underlying == typeof(DateTimeOffset)
               || underlying == typeof(TimeSpan)
               || underlying == typeof(Guid)
               || underlying == typeof(byte[]);
    }
}
