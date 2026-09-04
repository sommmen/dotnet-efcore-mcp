using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;

namespace DotnetEfCoreMcp.Server.Mutations;

public sealed class EntityMutationExecutor(ILogger<EntityMutationExecutor> logger)
{
    internal async Task<EntityMutationResult> ExecuteAsync(
        DbContext context,
        EntityMutationRequest request,
        CancellationToken cancellationToken)
    {
        var entityType = ResolveEntityType(context, request.EntityName);
        var primaryKey = entityType.FindPrimaryKey()
            ?? throw new MutationExecutionException($"Entity '{request.EntityName}' has no primary key and cannot be mutated.");

        var keyValues = request.Operation is EntityMutationOperation.Insert
            ? null
            : ValidateKey(primaryKey, request.Key);
        var values = ValidateValues(entityType, primaryKey, request.Operation, request.Values);

        if (request.Operation is EntityMutationOperation.Insert)
        {
            ValidateInsertRequiredProperties(entityType, values);
            return await InsertAsync(context, entityType, values, cancellationToken);
        }

        var concurrency = ValidateConcurrency(entityType, request.Concurrency);
        var entity = await context.FindAsync(entityType.ClrType, keyValues!, cancellationToken);
        if (entity is null)
        {
            return Conflict(entityType);
        }

        var entry = context.Entry(entity);
        ApplyOriginalConcurrencyValues(entry, concurrency);

        if (request.Operation is EntityMutationOperation.Update)
        {
            foreach (var (property, value) in values)
            {
                entry.Property(property.Name).CurrentValue = value;
                entry.Property(property.Name).IsModified = true;
            }
        }
        else
        {
            entry.State = EntityState.Deleted;
        }

        try
        {
            var affectedRows = await context.SaveChangesAsync(cancellationToken);
            return affectedRows == 0
                ? Conflict(entityType)
                : new EntityMutationResult(
                    entityType.ClrType.Name,
                    request.Operation.ToString().ToLowerInvariant(),
                    affectedRows,
                    request.Operation is EntityMutationOperation.Delete ? null : ReadValues(entry));
        }
        catch (DbUpdateConcurrencyException ex)
        {
            logger.LogWarning(ex, "Entity {Entity} mutation resulted in a concurrency conflict.", entityType.ClrType.Name);
            return Conflict(entityType);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Entity {Entity} mutation failed.", entityType.ClrType.Name);
            throw new MutationExecutionException("The entity mutation could not be completed.", innerException: ex);
        }
    }

    private static async Task<EntityMutationResult> InsertAsync(
        DbContext context,
        IEntityType entityType,
        IReadOnlyDictionary<IProperty, object?> values,
        CancellationToken cancellationToken)
    {
        var entity = Activator.CreateInstance(entityType.ClrType)
            ?? throw new MutationExecutionException($"Entity '{entityType.ClrType.Name}' cannot be created.");
        var entry = context.Add(entity);
        foreach (var (property, value) in values)
        {
            entry.Property(property.Name).CurrentValue = value;
        }

        try
        {
            var affectedRows = await context.SaveChangesAsync(cancellationToken);
            return new EntityMutationResult(entityType.ClrType.Name, "insert", affectedRows, ReadValues(entry));
        }
        catch (DbUpdateException ex)
        {
            throw new MutationExecutionException("The entity mutation could not be completed.", innerException: ex);
        }
    }

    private static IEntityType ResolveEntityType(DbContext context, string entityName)
    {
        if (string.IsNullOrWhiteSpace(entityName))
        {
            throw new MutationExecutionException("An entity name is required.");
        }

        var matches = context.Model.GetEntityTypes()
            .Where(candidate => string.Equals(candidate.ClrType.Name, entityName, StringComparison.Ordinal)
                || string.Equals(candidate.ClrType.FullName, entityName, StringComparison.Ordinal))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new MutationExecutionException($"Entity '{entityName}' was not found in the DbContext model."),
            _ => throw new MutationExecutionException($"Entity name '{entityName}' is ambiguous. Use its fully qualified CLR type name.")
        };
    }

    private static object?[] ValidateKey(IKey primaryKey, IReadOnlyDictionary<string, JsonElement>? key)
    {
        if (key is null || key.Count != primaryKey.Properties.Count)
        {
            throw new MutationExecutionException("Complete primary-key values are required.");
        }

        var unknown = key.Keys.Except(primaryKey.Properties.Select(property => property.Name), StringComparer.Ordinal).FirstOrDefault();
        if (unknown is not null)
        {
            throw new MutationExecutionException($"'{unknown}' is not a primary-key property.");
        }

        return primaryKey.Properties.Select(property =>
        {
            if (property.IsShadowProperty())
            {
                throw new MutationExecutionException($"Shadow property '{property.Name}' cannot be supplied.");
            }
            return ConvertValue(property, key[property.Name]);
        }).ToArray();
    }

    private static IReadOnlyDictionary<IProperty, object?> ValidateValues(
        IEntityType entityType,
        IKey primaryKey,
        EntityMutationOperation operation,
        IReadOnlyDictionary<string, JsonElement>? suppliedValues)
    {
        if (operation is EntityMutationOperation.Delete)
        {
            if (suppliedValues is { Count: > 0 })
            {
                throw new MutationExecutionException("Delete does not accept property values.");
            }
            return new Dictionary<IProperty, object?>();
        }

        if (suppliedValues is null || (operation is EntityMutationOperation.Update && suppliedValues.Count == 0))
        {
            throw new MutationExecutionException(operation is EntityMutationOperation.Insert
                ? "Insert requires property values."
                : "Update requires a non-empty set of property values.");
        }

        var values = new Dictionary<IProperty, object?>();
        foreach (var (name, value) in suppliedValues)
        {
            var property = ResolveWritableProperty(entityType, name);
            if (operation is EntityMutationOperation.Update && primaryKey.Properties.Contains(property))
            {
                throw new MutationExecutionException($"Primary-key property '{name}' cannot be changed.");
            }
            if (IsStoreGeneratedOrReadOnly(property, operation))
            {
                throw new MutationExecutionException($"Property '{name}' is store-generated or read-only and cannot be supplied.");
            }
            values.Add(property, ConvertValue(property, value));
        }
        return values;
    }

    private static IProperty ResolveWritableProperty(IEntityType entityType, string name)
    {
        var property = entityType.FindProperty(name);
        if (property is null)
        {
            if (entityType.FindNavigation(name) is not null || entityType.FindSkipNavigation(name) is not null)
            {
                throw new MutationExecutionException($"'{name}' is a navigation property, not a writable scalar property.");
            }
            if (entityType.FindComplexProperty(name) is not null)
            {
                throw new MutationExecutionException($"'{name}' is a complex property, not a writable scalar property.");
            }
            throw new MutationExecutionException($"Property '{name}' was not found on entity '{entityType.ClrType.Name}'.");
        }
        if (property.IsShadowProperty())
        {
            throw new MutationExecutionException($"Shadow property '{name}' cannot be supplied.");
        }
        return property;
    }

    private static bool IsStoreGeneratedOrReadOnly(IProperty property, EntityMutationOperation operation)
        => property.ValueGenerated is not ValueGenerated.Never
            || (operation is EntityMutationOperation.Insert && property.GetBeforeSaveBehavior() is not PropertySaveBehavior.Save)
            || (operation is EntityMutationOperation.Update && property.GetAfterSaveBehavior() is not PropertySaveBehavior.Save);

    private static void ValidateInsertRequiredProperties(IEntityType entityType, IReadOnlyDictionary<IProperty, object?> values)
    {
        foreach (var property in entityType.GetProperties())
        {
            if (!property.IsNullable && property.ValueGenerated is ValueGenerated.Never && !values.ContainsKey(property))
            {
                throw new MutationExecutionException($"Required property '{property.Name}' must be supplied for insert.");
            }
        }
    }

    private static IReadOnlyDictionary<IProperty, object?> ValidateConcurrency(
        IEntityType entityType,
        IReadOnlyDictionary<string, JsonElement>? suppliedConcurrency)
    {
        var tokens = entityType.GetProperties().Where(property => property.IsConcurrencyToken).ToArray();
        if (tokens.Length == 0)
        {
            if (suppliedConcurrency is { Count: > 0 })
            {
                throw new MutationExecutionException("This entity has no concurrency-token properties.");
            }
            return new Dictionary<IProperty, object?>();
        }

        if (suppliedConcurrency is null || suppliedConcurrency.Count != tokens.Length)
        {
            throw new MutationExecutionException("Original values for every concurrency-token property are required.");
        }
        var values = new Dictionary<IProperty, object?>();
        foreach (var token in tokens)
        {
            if (!suppliedConcurrency.TryGetValue(token.Name, out var value))
            {
                throw new MutationExecutionException($"Concurrency token '{token.Name}' is required.");
            }
            values.Add(token, ConvertValue(token, value));
        }
        if (suppliedConcurrency.Keys.Any(name => !tokens.Any(token => token.Name == name)))
        {
            throw new MutationExecutionException("Concurrency values contain an unknown property.");
        }
        return values;
    }

    private static void ApplyOriginalConcurrencyValues(EntityEntry entry, IReadOnlyDictionary<IProperty, object?> concurrency)
    {
        foreach (var (property, value) in concurrency)
        {
            entry.Property(property.Name).OriginalValue = value;
        }
    }

    private static object? ConvertValue(IProperty property, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null)
        {
            if (!property.IsNullable && Nullable.GetUnderlyingType(property.ClrType) is null)
            {
                throw new MutationExecutionException($"Property '{property.Name}' does not allow null values.");
            }
            return null;
        }

        try
        {
            var converter = property.GetValueConverter();
            var targetType = converter?.ProviderClrType ?? property.ClrType;
            var converted = value.Deserialize(targetType);
            return converter is null ? converted : converter.ConvertFromProvider(converted);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or FormatException)
        {
            throw new MutationExecutionException($"Value for property '{property.Name}' is not valid for its CLR type.", innerException: ex);
        }
    }

    private static IReadOnlyDictionary<string, object?> ReadValues(EntityEntry entry)
        => entry.Metadata.GetProperties()
            .Where(property => !property.IsShadowProperty())
            .ToDictionary(property => property.Name, property => entry.Property(property.Name).CurrentValue);

    private static EntityMutationResult Conflict(IEntityType entityType)
        => new(entityType.ClrType.Name, "conflict", 0, IsConflict: true);
}
