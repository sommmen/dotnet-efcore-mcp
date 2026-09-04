using System.Text.Json;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Compilation;
using DotnetEfCoreMcp.Server.Querying;

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
if (args.Length > 0 && string.Equals(args[0], "--persistent", StringComparison.OrdinalIgnoreCase))
{
    await RunPersistentAsync(GetPoolIdleTimeoutSeconds(args), jsonOptions);
    return;
}

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
catch (Exception ex)
{
    response = Error(requestId, $"The out-of-process query host could not execute the query: {ex.GetType().Name}: {ex.Message}");
}

await Console.Out.WriteAsync(JsonSerializer.Serialize(response, jsonOptions));

static async Task RunPersistentAsync(int poolIdleTimeoutSeconds, JsonSerializerOptions jsonOptions)
{
    var idleTimeout = TimeSpan.FromSeconds(Math.Max(1, poolIdleTimeoutSeconds) * 2);
    var assemblyLoader = new AssemblyLoaderService();
    LoadedAssemblyHandle? loadedTarget = null;
    string? loadedTargetPath = null;
    var compiler = new QueryCompiler(new QueryCompilationOptions());
    var lastActivityTicks = DateTimeOffset.UtcNow.UtcTicks;
    var inFlight = 0;
    using var shutdownCts = new CancellationTokenSource();
    var idleMonitor = Task.Run(async () =>
    {
        try
        {
            while (!shutdownCts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), shutdownCts.Token);
                if (Interlocked.CompareExchange(ref inFlight, 0, 0) != 0)
                    continue;

                var lastActivityUtc = new DateTimeOffset(Interlocked.Read(ref lastActivityTicks), TimeSpan.Zero);
                if (DateTimeOffset.UtcNow - lastActivityUtc >= idleTimeout)
                {
                    Environment.Exit(0);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }, shutdownCts.Token);

    try
    {
        while (true)
        {
            var line = await Console.In.ReadLineAsync();
            if (line is null)
                return;

            Interlocked.Exchange(ref lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);
            if (IsShutdown(line, jsonOptions))
                return;

            Interlocked.Exchange(ref inFlight, 1);
            var requestId = string.Empty;
            OutOfProcessQueryResponse response;
            try
            {
                var request = JsonSerializer.Deserialize<OutOfProcessQueryRequest>(line, jsonOptions)
                    ?? throw new QueryExecutionException("The query host request was invalid.");
                requestId = request.RequestId;
                if (request.ProtocolVersion != OutOfProcessQueryRequest.CurrentProtocolVersion)
                    throw new QueryExecutionException("The query host request uses an unsupported protocol version.");

                if (!string.Equals(loadedTargetPath, request.TargetAssemblyPath, StringComparison.Ordinal))
                {
                    loadedTarget = assemblyLoader.Load(request.TargetAssemblyPath);
                    loadedTargetPath = request.TargetAssemblyPath;
                }

                var contextType = loadedTarget!.Assembly.GetType(request.ContextTypeName, throwOnError: false)
                    ?? throw new QueryExecutionException("The requested DbContext type was not found in the target assembly.");
                var executor = new RoslynQueryExecutor(request.Options, compiler);
                var result = await executor.ExecuteAsync(loadedTarget, contextType, request.Connection, request.Provider, request.Query, CancellationToken.None);
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
            catch (Exception ex)
            {
                response = Error(requestId, $"The out-of-process query host could not execute the query: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref inFlight, 0);
            }

            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, jsonOptions));
            await Console.Out.FlushAsync();
            Interlocked.Exchange(ref lastActivityTicks, DateTimeOffset.UtcNow.UtcTicks);
        }
    }
    finally
    {
        shutdownCts.Cancel();
        try
        {
            await idleMonitor.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

static bool IsShutdown(string line, JsonSerializerOptions jsonOptions)
{
    try
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.TryGetProperty("requestId", out var requestIdProperty) && requestIdProperty.ValueKind == JsonValueKind.Null)
            return true;

        if (root.TryGetProperty("type", out var typeProperty) &&
            string.Equals(typeProperty.GetString(), "shutdown", StringComparison.OrdinalIgnoreCase))
        {
            var shutdown = JsonSerializer.Deserialize<OutOfProcessShutdownRequest>(line, jsonOptions);
            return shutdown?.ProtocolVersion == OutOfProcessQueryRequest.CurrentProtocolVersion;
        }
    }
    catch (JsonException)
    {
        return false;
    }

    return false;
}

static int GetPoolIdleTimeoutSeconds(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (!string.Equals(args[i], "--idle-timeout-seconds", StringComparison.OrdinalIgnoreCase))
            continue;

        if (int.TryParse(args[i + 1], out var value) && value > 0)
            return value;
    }

    return 300;
}

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
