using System.Text.Json;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.Querying;

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var input = await Console.In.ReadToEndAsync();
var requestId = string.Empty;
OutOfProcessQueryResponse response;
try
{
    var request = JsonSerializer.Deserialize<OutOfProcessQueryRequest>(input, jsonOptions)
        ?? throw new QueryExecutionException("The query host request was invalid.");
    requestId = request.RequestId;
    if (request.ProtocolVersion != OutOfProcessQueryRequest.CurrentProtocolVersion)
        throw new QueryExecutionException("The query host request uses an unsupported protocol version.");

    var target = new AssemblyLoaderService().Load(request.TargetAssemblyPath);
    var contextType = target.Assembly.GetType(request.ContextTypeName, throwOnError: false)
        ?? throw new QueryExecutionException("The requested DbContext type was not found in the target assembly.");
    var executor = new RoslynQueryExecutor(request.Options, new QueryCompiler(new QueryCompilationOptions()));
    var result = await executor.ExecuteAsync(target, contextType, request.Connection, request.Provider, request.Query, CancellationToken.None);
    response = new OutOfProcessQueryResponse
    {
        ProtocolVersion = OutOfProcessQueryRequest.CurrentProtocolVersion,
        RequestId = request.RequestId,
        Result = ToWire(result),
    };
}
catch (QueryExecutionException ex)
{
    response = Error(requestId, ex.Message);
}
catch
{
    response = Error(requestId, "The out-of-process query host could not execute the query.");
}

await Console.Out.WriteAsync(JsonSerializer.Serialize(response, jsonOptions));

static OutOfProcessQueryResponse Error(string requestId, string error) => new()
{
    ProtocolVersion = OutOfProcessQueryRequest.CurrentProtocolVersion,
    RequestId = requestId,
    Error = error,
};

static QueryResultWire ToWire(QueryResult result) => new()
{
    Entity = result.Entity,
    RowCount = result.RowCount,
    EffectiveTake = result.EffectiveTake,
    HasMoreRows = result.HasMoreRows,
    IsScalar = result.IsScalar,
    Scalar = JsonSerializer.SerializeToElement(result.Scalar),
    Rows = result.Rows.Select(row => row.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value))).ToArray(),
};
