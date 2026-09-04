using System.Diagnostics;
using System.Text.Json;
using DotnetEfCoreMcp.Server.AssemblyLoading;
using DotnetEfCoreMcp.Server.Connections;

namespace DotnetEfCoreMcp.Server.Querying;

/// <summary>Runs the existing Roslyn execution pipeline in a short-lived, isolated child process.</summary>
public sealed class OutOfProcessRoslynQueryExecutor(QueryExecutionOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<QueryResult> ExecuteAsync(LoadedAssemblyHandle target, Type contextType, ConnectionRegistryEntry entry,
        DatabaseProvider provider, QueryRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.OutOfProcessHostPath))
            throw new QueryExecutionException("Out-of-process query execution requires QueryExecution:OutOfProcessHostPath.");

        var hostPath = Path.GetFullPath(options.OutOfProcessHostPath);
        if (!File.Exists(hostPath))
            throw new QueryExecutionException("The configured out-of-process query host was not found.");

        var runtimeConfigPath = Path.ChangeExtension(target.AssemblyPath, "runtimeconfig.json");
        if (!File.Exists(runtimeConfigPath))
            throw new QueryExecutionException("The target application must provide a runtime configuration file for out-of-process query execution.");

        var hostDepsFilePath = Path.ChangeExtension(hostPath, "deps.json");
        if (!File.Exists(hostDepsFilePath))
            throw new QueryExecutionException("The configured out-of-process query host is missing its dependency file.");

        var requestId = Guid.NewGuid().ToString("N");
        var payload = new OutOfProcessQueryRequest
        {
            RequestId = requestId,
            TargetAssemblyPath = target.AssemblyPath,
            ContextTypeName = contextType.FullName ?? contextType.Name,
            Connection = entry,
            Provider = provider,
            Query = request,
            Options = options,
        };

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                ArgumentList =
                {
                    "exec",
                    "--runtimeconfig", runtimeConfigPath,
                    "--depsfile", hostDepsFilePath,
                    hostPath,
                },
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        try
        {
            process.Start();
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(payload, JsonOptions)).ConfigureAwait(false);
            process.StandardInput.Close();
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
                throw new QueryExecutionException("The out-of-process query host failed to execute the query.");

            var response = JsonSerializer.Deserialize<OutOfProcessQueryResponse>(stdout, JsonOptions);
            if (response is null || response.ProtocolVersion != OutOfProcessQueryRequest.CurrentProtocolVersion || response.RequestId != requestId)
                throw new QueryExecutionException("The out-of-process query host returned an invalid response.");
            if (!string.IsNullOrWhiteSpace(response.Error))
                throw new QueryExecutionException(response.Error);
            if (response.Result is null)
                throw new QueryExecutionException("The out-of-process query host did not return a result.");

            return ToQueryResult(response.Result);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        catch (QueryExecutionException) { throw; }
        catch (Exception ex)
        {
            throw new QueryExecutionException("Unable to execute the query in the out-of-process query host.", ex);
        }
    }

    private static QueryResult ToQueryResult(QueryResultWire result) => new(
        result.Entity, result.RowCount, result.EffectiveTake, result.HasMoreRows, result.IsScalar,
        FromJson(result.Scalar), result.Rows.Select(row => (IReadOnlyDictionary<string, object?>)row.ToDictionary(pair => pair.Key, pair => FromJson(pair.Value))).ToArray());

    private static object? FromJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt32(out var intValue) => intValue,
        JsonValueKind.Number when value.TryGetInt64(out var longValue) => longValue,
        JsonValueKind.Number when value.TryGetDecimal(out var decimalValue) => decimalValue,
        _ => value.Clone(),
    };
}
