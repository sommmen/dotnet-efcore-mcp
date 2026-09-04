using System.Text.Json;

namespace DotnetEfCoreMcp.Server.Mutations;

internal enum EntityMutationOperation
{
    Insert,
    Update,
    Delete
}

internal sealed record EntityMutationRequest(
    EntityMutationOperation Operation,
    string EntityName,
    IReadOnlyDictionary<string, JsonElement>? Key = null,
    IReadOnlyDictionary<string, JsonElement>? Values = null,
    IReadOnlyDictionary<string, JsonElement>? Concurrency = null);

internal sealed record EntityMutationResult(
    string Entity,
    string Operation,
    int AffectedRows,
    IReadOnlyDictionary<string, object?>? Values = null,
    bool IsConflict = false);

internal sealed class MutationExecutionException(string message, bool isConflict = false, Exception? innerException = null)
    : Exception(message, innerException)
{
    public bool IsConflict { get; } = isConflict;
}
