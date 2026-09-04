namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>Chooses where Roslyn user queries execute.</summary>
public enum QueryExecutionMode
{
    InProcess,
    OutOfProcess,
    Pooled,
    Auto,
}
