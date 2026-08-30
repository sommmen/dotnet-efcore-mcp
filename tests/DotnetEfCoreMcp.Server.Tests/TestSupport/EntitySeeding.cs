using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace DotnetEfCoreMcp.Server.Tests.TestSupport;

/// <summary>Small reflection helpers for constructing and reading fixture entities whose CLR
/// types aren't available at the test project's compile time (the SampleApp fixture is
/// intentionally not a ProjectReference - see the test project's csproj).</summary>
public static class EntitySeeding
{
    public static object CreateEntity(Type entityType, IReadOnlyDictionary<string, object?> propertyValues)
    {
        var entity = Activator.CreateInstance(entityType)
            ?? throw new InvalidOperationException($"Could not construct '{entityType.FullName}'.");

        foreach (var (name, value) in propertyValues)
        {
            var property = entityType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"Property '{name}' not found on '{entityType.FullName}'.");
            property.SetValue(entity, value);
        }

        return entity;
    }

    public static Type GetEntityClrType(DbContext context, string entityName) =>
        context.Model.GetEntityTypes().First(e => e.ClrType.Name == entityName).ClrType;

    public static object? GetPropertyValue(object entity, string propertyName) =>
        entity.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(entity);
}
