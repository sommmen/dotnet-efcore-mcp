using System.Text.Json;
using DotnetEfCoreMcp.Server.Connections;

namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>Versioned stdin/stdout payload exchanged with the isolated Roslyn query host.</summary>
public sealed class OutOfProcessQueryRequest
{
    public const int CurrentProtocolVersion = 1;
    public int ProtocolVersion { get; init; } = CurrentProtocolVersion;
    public required string RequestId { get; init; }
    public required string TargetAssemblyPath { get; init; }
    public required string ContextTypeName { get; init; }
    public required ConnectionRegistryEntry Connection { get; init; }
    public required DatabaseProvider Provider { get; init; }
    public required QueryRequest Query { get; init; }
    public required QueryExecutionOptions Options { get; init; }
}

public sealed class OutOfProcessQueryResponse
{
    public int ProtocolVersion { get; init; }
    public required string RequestId { get; init; }
    public QueryResultWire? Result { get; init; }
    public string? Error { get; init; }
}

public sealed class QueryResultWire
{
    public required string Entity { get; init; }
    public int RowCount { get; init; }
    public int? EffectiveTake { get; init; }
    public bool HasMoreRows { get; init; }
    public bool IsScalar { get; init; }
    public JsonElement Scalar { get; init; }
    public required IReadOnlyList<Dictionary<string, JsonElement>> Rows { get; init; }
}
