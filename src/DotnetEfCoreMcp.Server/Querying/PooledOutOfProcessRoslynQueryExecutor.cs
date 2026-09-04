using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;

namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>Runs Roslyn execution in a bounded pool of persistent isolated query-host workers.</summary>
public sealed class PooledOutOfProcessRoslynQueryExecutor(QueryHostPool pool)
{
    public Task<QueryResult> ExecuteAsync(
        LoadedAssemblyHandle target,
        Type contextType,
        ConnectionRegistryEntry entry,
        DatabaseProvider provider,
        QueryRequest request,
        CancellationToken cancellationToken)
        => pool.ExecuteAsync(target, contextType, entry, provider, request, cancellationToken);
}
