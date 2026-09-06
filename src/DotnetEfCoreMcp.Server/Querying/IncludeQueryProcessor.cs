using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DotnetEfCoreMcp.Server.Querying;

internal sealed class IncludePlan(IEntityType rootType, IReadOnlyList<IncludePath> paths)
{
    public IEntityType RootType { get; } = rootType;
    public IReadOnlyList<IncludePath> Paths { get; } = paths;
}

internal sealed class IncludePath(IReadOnlyList<INavigationBase> navigations)
{
    public IReadOnlyList<INavigationBase> Navigations { get; } = navigations;
}

internal static class IncludeQueryProcessor
{
    private static readonly MethodInfo IncludeMethod = typeof(EntityFrameworkQueryableExtensions).GetMethods()
        .Single(m => m.Name == nameof(EntityFrameworkQueryableExtensions.Include) &&
                     m.GetParameters().Length == 2 &&
                     m.GetParameters()[1].ParameterType.IsGenericType &&
                     m.GetParameters()[1].ParameterType.GetGenericTypeDefinition() == typeof(Expression<>));

    public static (IQueryable Query, IncludePlan? Plan) Apply(IQueryable query, DbContext context, IReadOnlyList<string>? includePaths, QueryExecutionOptions options)
    {
        if (includePaths is not { Count: > 0 }) return (query, null);
        if (options.MaxIncludeDepth <= 0) throw new QueryExecutionException("The server-configured MaxIncludeDepth value must be positive.");
        if (options.MaxIncludeCount <= 0) throw new QueryExecutionException("The server-configured MaxIncludeCount value must be positive.");
        if (options.MaxIncludedCollectionItems < 0) throw new QueryExecutionException("The server-configured MaxIncludedCollectionItems value must not be negative.");

        var root = context.Model.FindEntityType(query.ElementType)
            ?? throw new QueryExecutionException("Include paths require a query returning a mapped entity type.");
        if (includePaths.Count > options.MaxIncludeCount)
            throw new QueryExecutionException($"Include count {includePaths.Count} exceeds the configured maximum of {options.MaxIncludeCount} (MaxIncludeCount).");

        var parsed = new List<IncludePath>();
        foreach (var includePath in includePaths)
        {
            if (string.IsNullOrWhiteSpace(includePath)) throw new QueryExecutionException("Include paths must be non-empty dot-separated navigation paths.");
            var segments = includePath.Split('.', StringSplitOptions.None);
            if (segments.Length > options.MaxIncludeDepth)
                throw new QueryExecutionException($"Include path depth {segments.Length} exceeds the configured maximum of {options.MaxIncludeDepth} (MaxIncludeDepth).");
            if (segments.Any(string.IsNullOrWhiteSpace)) throw new QueryExecutionException("Include paths must not contain empty navigation segments.");

            var current = root;
            var seenTypes = new HashSet<IEntityType> { root };
            var seenNavigations = new HashSet<INavigationBase>();
            var navigations = new List<INavigationBase>();
            foreach (var segment in segments)
            {
                INavigationBase? navigation = current.FindNavigation(segment);
                navigation ??= current.FindSkipNavigation(segment);
                if (navigation is null) throw new QueryExecutionException($"Include path segment '{segment}' is not a navigation on '{current.Name}'.");
                if (navigation.PropertyInfo is null)
                    throw new QueryExecutionException($"Include path segment '{segment}' is not backed by a readable CLR property.");
                if (!seenNavigations.Add(navigation))
                    throw new QueryExecutionException("Include paths must not traverse the same navigation more than once.");
                current = navigation.TargetEntityType;
                if (!seenTypes.Add(current)) throw new QueryExecutionException("Include paths must not contain navigation cycles.");
                navigations.Add(navigation);
            }
            parsed.Add(new IncludePath(navigations));
        }

        if (parsed.Select(p => string.Join('.', p.Navigations.Select(n => n.Name))).Distinct(StringComparer.Ordinal).Count() != parsed.Count)
            throw new QueryExecutionException("Include paths must not contain duplicates.");

        IQueryable composed = query;
        foreach (var path in parsed) composed = ApplyPath(composed, path, options.MaxIncludedCollectionItems);

        if (parsed.Any(path => path.Navigations.Any(navigation => navigation.IsCollection)))
            composed = ApplySplitQuery(composed);

        return (composed, new IncludePlan(root, parsed));
    }

    public static Dictionary<string, object?> Project(object entity, IncludePlan plan)
        => ProjectEntity(entity, plan.RootType, plan.Paths.Select(p => p.Navigations).ToArray());

    private static IQueryable ApplyPath(IQueryable query, IncludePath path, int cap)
    {
        object current = query;
        var rootType = query.ElementType;
        for (var i = 0; i < path.Navigations.Count; i++)
        {
            var navigation = path.Navigations[i];
            var lambda = BuildNavigationLambda(i == 0 ? rootType : path.Navigations[i - 1].TargetEntityType.ClrType, navigation, cap);
            if (i == 0)
            {
                current = IncludeMethod.MakeGenericMethod(rootType, lambda.ReturnType).Invoke(null, [current, lambda])!;
            }
            else
            {
                var thenInclude = FindThenInclude(path.Navigations[i - 1].IsCollection);
                current = thenInclude.MakeGenericMethod(rootType, navigation.DeclaringEntityType.ClrType, lambda.ReturnType).Invoke(null, [current, lambda])!;
            }
        }
        return (IQueryable)current;
    }

    private static IQueryable ApplySplitQuery(IQueryable query)
        => (IQueryable)typeof(RelationalQueryableExtensions).GetMethods()
            .Single(method => method.Name == nameof(RelationalQueryableExtensions.AsSplitQuery) && method.GetParameters().Length == 1)
            .MakeGenericMethod(query.ElementType)
            .Invoke(null, [query])!;

    private static MethodInfo FindThenInclude(bool previousIsCollection)
        => typeof(EntityFrameworkQueryableExtensions).GetMethods().Single(m =>
        {
            if (m.Name != nameof(EntityFrameworkQueryableExtensions.ThenInclude) || m.GetParameters().Length != 2) return false;
            var prior = m.GetParameters()[0].ParameterType.GetGenericArguments()[1];
            return previousIsCollection == (prior.IsGenericType && prior.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        });

    private static LambdaExpression BuildNavigationLambda(Type declaringType, INavigationBase navigation, int cap)
    {
        var parameter = Expression.Parameter(declaringType, "e");
        Expression body = Expression.Property(parameter, navigation.PropertyInfo!);
        if (navigation.IsCollection)
        {
            var elementType = navigation.TargetEntityType.ClrType;
            var primaryKey = navigation.TargetEntityType.FindPrimaryKey()
                ?? throw new QueryExecutionException($"Included collection '{navigation.Name}' must have a primary key for deterministic ordering.");
            var keyParameter = Expression.Parameter(elementType, "x");
            for (var i = 0; i < primaryKey.Properties.Count; i++)
            {
                var property = primaryKey.Properties[i];
                if (property.PropertyInfo is null)
                    throw new QueryExecutionException($"Included collection '{navigation.Name}' has a key not backed by a CLR property.");
                var keySelector = Expression.Lambda(Expression.Property(keyParameter, property.PropertyInfo), keyParameter);
                var methodName = i == 0 ? nameof(Enumerable.OrderBy) : nameof(Enumerable.ThenBy);
                var method = typeof(Enumerable).GetMethods().Single(m => m.Name == methodName && m.GetParameters().Length == 2)
                    .MakeGenericMethod(elementType, property.ClrType);
                body = Expression.Call(method, body, keySelector);
            }
            body = Expression.Call(typeof(Enumerable), nameof(Enumerable.Take), [elementType], body, Expression.Constant(cap));
        }
        return Expression.Lambda(body, parameter);
    }

    private static Dictionary<string, object?> ProjectEntity(object entity, IEntityType entityType, IReadOnlyList<IReadOnlyList<INavigationBase>> paths)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in entityType.GetProperties())
            result[property.Name] = property.PropertyInfo?.GetValue(entity);

        foreach (var group in paths.Where(p => p.Count > 0).GroupBy(p => p[0]))
        {
            var navigation = group.Key;
            var value = navigation.PropertyInfo?.GetValue(entity);
            var descendants = group.Select(p => (IReadOnlyList<INavigationBase>)p.Skip(1).ToArray()).ToArray();
            if (value is null) { result[navigation.Name] = null; continue; }
            if (navigation.IsCollection)
                result[navigation.Name] = ((IEnumerable)value).Cast<object>().Select(item => ProjectEntity(item, navigation.TargetEntityType, descendants)).ToList();
            else
                result[navigation.Name] = ProjectEntity(value, navigation.TargetEntityType, descendants);
        }
        return result;
    }
}
